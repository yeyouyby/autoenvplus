#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <charconv>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/base.h>

namespace fs = std::filesystem;
using winrt::Windows::Data::Json::JsonArray;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValueType;

namespace {

constexpr int kMaximumRecursionDepth = 4;

struct ShimError final : std::runtime_error {
    ShimError(std::string message, int code)
        : std::runtime_error(std::move(message)), exit_code(code) {}

    int exit_code;
};

std::wstring ToLower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t character) {
        return static_cast<wchar_t>(std::towlower(character));
    });
    return value;
}

std::string ToLowerAscii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return character >= 'A' && character <= 'Z'
            ? static_cast<char>(character - 'A' + 'a')
            : static_cast<char>(character);
    });
    return value;
}

bool EqualsIgnoreCase(std::wstring_view left, std::wstring_view right) {
    if (left.size() != right.size()) {
        return false;
    }
    return CompareStringOrdinal(
               left.data(), static_cast<int>(left.size()),
               right.data(), static_cast<int>(right.size()), TRUE) == CSTR_EQUAL;
}

std::string WideToUtf8(std::wstring_view value) {
    if (value.empty()) {
        return {};
    }
    const int size = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS,
        value.data(), static_cast<int>(value.size()),
        nullptr, 0, nullptr, nullptr);
    if (size <= 0) {
        throw std::runtime_error("Unable to encode a UTF-8 diagnostic message.");
    }
    std::string output(static_cast<size_t>(size), '\0');
    if (WideCharToMultiByte(
            CP_UTF8, WC_ERR_INVALID_CHARS,
            value.data(), static_cast<int>(value.size()),
            output.data(), size, nullptr, nullptr) != size) {
        throw std::runtime_error("Unable to encode a UTF-8 diagnostic message.");
    }
    return output;
}

std::wstring Utf8ToWide(std::string_view value) {
    if (value.empty()) {
        return {};
    }
    const int size = MultiByteToWideChar(
        CP_UTF8, MB_ERR_INVALID_CHARS,
        value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (size <= 0) {
        throw ShimError("A state file is not valid UTF-8.", 70);
    }
    std::wstring output(static_cast<size_t>(size), L'\0');
    if (MultiByteToWideChar(
            CP_UTF8, MB_ERR_INVALID_CHARS,
            value.data(), static_cast<int>(value.size()),
            output.data(), size) != size) {
        throw ShimError("A state file is not valid UTF-8.", 70);
    }
    return output;
}

std::wstring ReadUtf8File(const fs::path& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        throw ShimError("Unable to read state file: " + WideToUtf8(path.wstring()), 70);
    }
    std::string bytes(
        (std::istreambuf_iterator<char>(input)),
        std::istreambuf_iterator<char>());
    if (bytes.size() >= 3
        && static_cast<unsigned char>(bytes[0]) == 0xEF
        && static_cast<unsigned char>(bytes[1]) == 0xBB
        && static_cast<unsigned char>(bytes[2]) == 0xBF) {
        bytes.erase(0, 3);
    }
    return Utf8ToWide(bytes);
}

std::wstring GetModulePath() {
    std::vector<wchar_t> buffer(512);
    while (true) {
        const DWORD length = GetModuleFileNameW(
            nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length == 0) {
            throw ShimError("The native Shim cannot determine its executable path.", 70);
        }
        if (length < buffer.size() - 1) {
            return std::wstring(buffer.data(), length);
        }
        buffer.resize(buffer.size() * 2);
    }
}

std::wstring GetEnvironment(std::wstring_view name) {
    const DWORD required = GetEnvironmentVariableW(
        std::wstring(name).c_str(), nullptr, 0);
    if (required == 0) {
        return {};
    }
    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(
        std::wstring(name).c_str(), value.data(), required);
    if (written == 0 || written >= required) {
        return {};
    }
    value.resize(written);
    return value;
}

void WriteError(std::string_view message) {
    HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
    if (error != INVALID_HANDLE_VALUE && error != nullptr) {
        DWORD written = 0;
        WriteFile(error, message.data(), static_cast<DWORD>(message.size()), &written, nullptr);
        static constexpr char newline[] = "\r\n";
        WriteFile(error, newline, 2, &written, nullptr);
    }
}

fs::path FullPath(const fs::path& value) {
    std::error_code error;
    fs::path full = fs::absolute(value, error).lexically_normal();
    if (error) {
        throw ShimError("Unable to normalize path: " + WideToUtf8(value.wstring()), 70);
    }
    return full;
}

bool IsChildPath(const fs::path& root, const fs::path& candidate) {
    std::wstring root_text = FullPath(root).wstring();
    std::wstring candidate_text = FullPath(candidate).wstring();
    while (root_text.size() > 3 && (root_text.back() == L'\\' || root_text.back() == L'/')) {
        root_text.pop_back();
    }
    root_text.push_back(L'\\');
    return candidate_text.size() > root_text.size()
        && CompareStringOrdinal(
               candidate_text.data(), static_cast<int>(root_text.size()),
               root_text.data(), static_cast<int>(root_text.size()), TRUE) == CSTR_EQUAL;
}

fs::path ResolveInside(
    const fs::path& root,
    const fs::path& relative,
    std::string_view description) {
    if (relative.is_absolute() || relative.has_root_name() || relative.has_root_directory()) {
        throw ShimError(
            "The " + std::string(description) + " must be relative.", 70);
    }
    fs::path candidate = FullPath(root / relative);
    if (!IsChildPath(root, candidate)) {
        throw ShimError(
            "The " + std::string(description) + " escaped its managed root.", 70);
    }
    return candidate;
}

bool FileExists(const fs::path& path) {
    std::error_code error;
    return fs::is_regular_file(path, error) && !error;
}

struct Version {
    int major = 0;
    int minor = 0;
    int patch = 0;
    int revision = 0;
    std::string prerelease;

    static std::optional<Version> Parse(std::string value) {
        if (value.empty()) {
            return std::nullopt;
        }
        if (value.front() == 'v' || value.front() == 'V') {
            value.erase(value.begin());
        }
        const size_t plus = value.find('+');
        if (plus != std::string::npos) {
            if (!ValidIdentifierText(value.substr(plus + 1))) {
                return std::nullopt;
            }
            value.resize(plus);
        }
        std::string prerelease;
        const size_t dash = value.find('-');
        if (dash != std::string::npos) {
            prerelease = value.substr(dash + 1);
            value.resize(dash);
            if (!ValidIdentifierText(prerelease)) {
                return std::nullopt;
            }
        }
        std::vector<int> numbers;
        size_t start = 0;
        while (start <= value.size()) {
            const size_t separator = value.find('.', start);
            const size_t end = separator == std::string::npos ? value.size() : separator;
            if (end == start || numbers.size() >= 4) {
                return std::nullopt;
            }
            int parsed = 0;
            const char* begin = value.data() + start;
            const char* finish = value.data() + end;
            const auto result = std::from_chars(begin, finish, parsed);
            if (result.ec != std::errc{} || result.ptr != finish || parsed < 0) {
                return std::nullopt;
            }
            numbers.push_back(parsed);
            if (separator == std::string::npos) {
                break;
            }
            start = separator + 1;
        }
        if (numbers.empty()) {
            return std::nullopt;
        }
        numbers.resize(4, 0);
        return Version{
            numbers[0], numbers[1], numbers[2], numbers[3], std::move(prerelease)};
    }

    std::string ToString() const {
        std::string value = std::to_string(major) + "." + std::to_string(minor)
            + "." + std::to_string(patch);
        if (revision != 0) {
            value += "." + std::to_string(revision);
        }
        if (!prerelease.empty()) {
            value += "-" + prerelease;
        }
        return value;
    }

    int Compare(const Version& other) const {
        if (major != other.major) return major < other.major ? -1 : 1;
        if (minor != other.minor) return minor < other.minor ? -1 : 1;
        if (patch != other.patch) return patch < other.patch ? -1 : 1;
        if (revision != other.revision) return revision < other.revision ? -1 : 1;
        if (prerelease.empty() && !other.prerelease.empty()) return 1;
        if (!prerelease.empty() && other.prerelease.empty()) return -1;
        return ComparePrerelease(prerelease, other.prerelease);
    }

private:
    static bool ValidIdentifierText(std::string_view value) {
        if (value.empty()) return false;
        return std::all_of(value.begin(), value.end(), [](unsigned char character) {
            return (character >= '0' && character <= '9')
                || (character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || character == '.' || character == '-';
        });
    }

    static int ComparePrerelease(std::string_view left, std::string_view right) {
        if (left.empty() && right.empty()) return 0;
        size_t left_start = 0;
        size_t right_start = 0;
        while (true) {
            const size_t left_dot = left.find('.', left_start);
            const size_t right_dot = right.find('.', right_start);
            const std::string_view left_part = left.substr(
                left_start, left_dot == std::string_view::npos
                    ? left.size() - left_start : left_dot - left_start);
            const std::string_view right_part = right.substr(
                right_start, right_dot == std::string_view::npos
                    ? right.size() - right_start : right_dot - right_start);
            int left_number = 0;
            int right_number = 0;
            const auto left_parse = std::from_chars(
                left_part.data(), left_part.data() + left_part.size(), left_number);
            const auto right_parse = std::from_chars(
                right_part.data(), right_part.data() + right_part.size(), right_number);
            const bool left_numeric = left_parse.ec == std::errc{}
                && left_parse.ptr == left_part.data() + left_part.size();
            const bool right_numeric = right_parse.ec == std::errc{}
                && right_parse.ptr == right_part.data() + right_part.size();
            int comparison = 0;
            if (left_numeric && right_numeric) {
                comparison = left_number == right_number ? 0 : (left_number < right_number ? -1 : 1);
            } else if (left_numeric != right_numeric) {
                comparison = left_numeric ? -1 : 1;
            } else {
                const std::string left_lower = LowerAscii(left_part);
                const std::string right_lower = LowerAscii(right_part);
                comparison = left_lower == right_lower ? 0 : (left_lower < right_lower ? -1 : 1);
            }
            if (comparison != 0) return comparison;
            const bool left_done = left_dot == std::string_view::npos;
            const bool right_done = right_dot == std::string_view::npos;
            if (left_done || right_done) {
                return left_done == right_done ? 0 : (left_done ? -1 : 1);
            }
            left_start = left_dot + 1;
            right_start = right_dot + 1;
        }
    }

    static std::string LowerAscii(std::string_view value) {
        std::string output(value);
        std::transform(output.begin(), output.end(), output.begin(), [](unsigned char character) {
            return character >= 'A' && character <= 'Z'
                ? static_cast<char>(character - 'A' + 'a')
                : static_cast<char>(character);
        });
        return output;
    }
};

enum class ConstraintKind { Auto, Major, MajorMinor, Exact, Channel };

struct Constraint {
    std::string raw;
    ConstraintKind kind = ConstraintKind::Auto;
    std::optional<Version> version;
    std::string channel;

    static Constraint Parse(std::string value) {
        auto trim = [](std::string& text) {
            const size_t first = text.find_first_not_of(" \t\r\n");
            if (first == std::string::npos) { text.clear(); return; }
            const size_t last = text.find_last_not_of(" \t\r\n");
            text = text.substr(first, last - first + 1);
        };
        trim(value);
        const std::string lower = LowerAscii(value);
        if (lower.empty() || lower == "auto") {
            return Constraint{"auto", ConstraintKind::Auto, std::nullopt, {}};
        }
        if (lower == "latest" || lower == "current" || lower == "lts") {
            return Constraint{lower, ConstraintKind::Channel, std::nullopt, lower};
        }
        const size_t dash = lower.find('-');
        if (dash != std::string::npos
            && (lower.substr(dash + 1) == "latest"
                || lower.substr(dash + 1) == "current"
                || lower.substr(dash + 1) == "lts")) {
            const std::string major_text = lower.substr(0, dash);
            int major = 0;
            const auto parsed = std::from_chars(
                major_text.data(), major_text.data() + major_text.size(), major);
            if (parsed.ec == std::errc{} && parsed.ptr == major_text.data() + major_text.size()) {
                return Constraint{
                    lower, ConstraintKind::Channel,
                    Version{major, 0, 0, 0, {}}, lower.substr(dash + 1)};
            }
        }
        const std::optional<Version> version = Version::Parse(value);
        if (!version) {
            throw ShimError("Unsupported runtime version selector: " + value, 70);
        }
        std::string numeric = value;
        if (!numeric.empty() && (numeric.front() == 'v' || numeric.front() == 'V')) {
            numeric.erase(numeric.begin());
        }
        const size_t suffix = numeric.find_first_of("-+");
        if (suffix != std::string::npos) numeric.resize(suffix);
        const size_t components = 1 + static_cast<size_t>(
            std::count(numeric.begin(), numeric.end(), '.'));
        return Constraint{
            value,
            components == 1 ? ConstraintKind::Major
                : components == 2 ? ConstraintKind::MajorMinor
                : ConstraintKind::Exact,
            version,
            {}};
    }

    bool Matches(const Version& candidate, const std::vector<std::string>& channels) const {
        switch (kind) {
        case ConstraintKind::Auto:
            return candidate.prerelease.empty();
        case ConstraintKind::Major:
            return candidate.major == version->major;
        case ConstraintKind::MajorMinor:
            return candidate.major == version->major && candidate.minor == version->minor;
        case ConstraintKind::Exact:
            return candidate.Compare(*version) == 0;
        case ConstraintKind::Channel:
            if (version && candidate.major != version->major) return false;
            return std::any_of(channels.begin(), channels.end(), [&](const std::string& value) {
                return LowerAscii(value) == channel;
            });
        }
        return false;
    }

private:
    static std::string LowerAscii(std::string value) {
        std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
            return character >= 'A' && character <= 'Z'
                ? static_cast<char>(character - 'A' + 'a')
                : static_cast<char>(character);
        });
        return value;
    }
};

std::wstring RequiredString(const JsonObject& object, std::wstring_view name) {
    const winrt::hstring key(name);
    if (!object.HasKey(key)) {
        throw ShimError("A state entry is missing required property '" + WideToUtf8(name) + "'.", 70);
    }
    const auto value = object.GetNamedValue(key);
    if (value.ValueType() != JsonValueType::String) {
        throw ShimError("State property '" + WideToUtf8(name) + "' must be a string.", 70);
    }
    return std::wstring(value.GetString());
}

int RequiredInteger(const JsonObject& object, std::wstring_view name) {
    const winrt::hstring key(name);
    if (!object.HasKey(key)) {
        throw ShimError("A state file is missing required property '" + WideToUtf8(name) + "'.", 70);
    }
    const auto value = object.GetNamedValue(key);
    if (value.ValueType() != JsonValueType::Number) {
        throw ShimError("State property '" + WideToUtf8(name) + "' must be a number.", 70);
    }
    const double number = value.GetNumber();
    const int integer = static_cast<int>(number);
    if (number != integer) {
        throw ShimError("State property '" + WideToUtf8(name) + "' must be an integer.", 70);
    }
    return integer;
}

JsonObject ParseJsonFile(const fs::path& path) {
    try {
        return JsonObject::Parse(winrt::hstring(ReadUtf8File(path)));
    } catch (const winrt::hresult_error& error) {
        throw ShimError(
            "Invalid JSON in " + WideToUtf8(path.wstring()) + ": "
                + WideToUtf8(error.message()),
            70);
    }
}

struct Installation {
    std::string id;
    std::string kind;
    Version version;
    std::string architecture;
    fs::path root;
    fs::path executable;
    std::vector<std::string> channels;
};

bool KnownRuntimeKind(std::string_view value) {
    static constexpr std::string_view kinds[] = {
        "python", "nodejs", "java", "dotnet", "msvc", "llvm", "mingw", "cmake", "ninja"};
    return std::find(std::begin(kinds), std::end(kinds), value) != std::end(kinds);
}

bool KnownArchitecture(std::string_view value) {
    return value == "x64" || value == "x86" || value == "arm64";
}

std::string CurrentArchitecture() {
#if defined(_M_X64)
    return "x64";
#elif defined(_M_IX86)
    return "x86";
#elif defined(_M_ARM64)
    return "arm64";
#else
#error Unsupported AutoEnvPlus Shim architecture.
#endif
}

bool ValidHexHash(std::string_view value, size_t expected_length) {
    return value.size() == expected_length
        && std::all_of(value.begin(), value.end(), [](unsigned char character) {
            return (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')
                || (character >= 'A' && character <= 'F');
        });
}

bool ValidPackageHash(std::string_view algorithm, std::string_view value) {
    const std::string normalized = ToLowerAscii(std::string(algorithm));
    if (normalized == "sha256" || normalized == "sha-256") {
        return ValidHexHash(value, 64);
    }
    if (normalized == "sha512" || normalized == "sha-512") {
        return ValidHexHash(value, 128);
    }
    return false;
}

std::vector<Installation> LoadRegistry(const fs::path& managed_root) {
    const fs::path registry_path = managed_root / L"state" / L"installations.json";
    if (!FileExists(registry_path)) {
        throw ShimError("No AutoEnvPlus managed runtimes are registered. Install a runtime first.", 69);
    }
    const JsonObject root = ParseJsonFile(registry_path);
    const int schema_version = RequiredInteger(root, L"schemaVersion");
    if (schema_version != 1 && schema_version != 2) {
        throw ShimError("The managed runtime registry schema is not supported.", 70);
    }
    if (!root.HasKey(L"installations")
        || root.GetNamedValue(L"installations").ValueType() != JsonValueType::Array) {
        throw ShimError("The managed runtime registry installations property must be an array.", 70);
    }
    std::vector<Installation> installations;
    for (const auto& value : root.GetNamedArray(L"installations")) {
        if (value.ValueType() != JsonValueType::Object) {
            throw ShimError("The managed runtime registry contains a non-object entry.", 70);
        }
        const JsonObject item = value.GetObject();
        const std::string id = WideToUtf8(RequiredString(item, L"id"));
        const std::string provider = WideToUtf8(RequiredString(item, L"providerId"));
        const std::string kind = ToLowerAscii(WideToUtf8(RequiredString(item, L"kind")));
        const std::string version_text = WideToUtf8(RequiredString(item, L"version"));
        const std::string architecture = ToLowerAscii(
            WideToUtf8(RequiredString(item, L"architecture")));
        const fs::path install_root = FullPath(RequiredString(item, L"installRoot"));
        const fs::path relative_executable = RequiredString(item, L"executableRelativePath");
        const std::string package_hash = WideToUtf8(RequiredString(
            item, schema_version == 1 ? L"packageSha256" : L"packageHash"));
        const std::string package_hash_algorithm = schema_version == 1
            ? "sha256"
            : WideToUtf8(RequiredString(item, L"packageHashAlgorithm"));
        const std::optional<Version> version = Version::Parse(version_text);
        if (id.empty() || provider.empty() || !KnownRuntimeKind(kind)
            || !version || !KnownArchitecture(architecture)
            || !ValidPackageHash(package_hash_algorithm, package_hash)
            || !IsChildPath(managed_root, install_root)) {
            throw ShimError("Managed runtime registry entry '" + id + "' is invalid.", 70);
        }
        std::vector<std::string> channels;
        if (item.HasKey(L"channels")) {
            const auto channel_value = item.GetNamedValue(L"channels");
            if (channel_value.ValueType() != JsonValueType::Array) {
                throw ShimError("Managed runtime channels must be an array.", 70);
            }
            for (const auto& channel : channel_value.GetArray()) {
                if (channel.ValueType() != JsonValueType::String) {
                    throw ShimError("Managed runtime channel entries must be strings.", 70);
                }
                channels.push_back(ToLowerAscii(WideToUtf8(channel.GetString())));
            }
        }
        installations.push_back(Installation{
            id, kind, *version, architecture, install_root,
            ResolveInside(install_root, relative_executable, "runtime executable"),
            std::move(channels)});
    }
    return installations;
}

std::string NormalizeToolKey(std::string value) {
    value = ToLowerAscii(std::move(value));
    if (value == "python") return "python";
    if (value == "node" || value == "nodejs" || value == "node.js") return "node";
    if (value == "java" || value == "jdk") return "java";
    if (value == "dotnet" || value == ".net") return "dotnet";
    if (value == "cmake") return "cmake";
    return {};
}

std::string StripComment(std::string_view line) {
    char quote = 0;
    for (size_t index = 0; index < line.size(); ++index) {
        const char character = line[index];
        if (character == '\'' || character == '"') {
            quote = quote == character ? 0 : (quote == 0 ? character : quote);
        } else if (character == '#' && quote == 0) {
            return std::string(line.substr(0, index));
        }
    }
    return std::string(line);
}

void Trim(std::string& value) {
    const size_t first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) { value.clear(); return; }
    const size_t last = value.find_last_not_of(" \t\r\n");
    value = value.substr(first, last - first + 1);
}

std::optional<std::string> ReadProjectSelector(
    const fs::path& start_path,
    std::string_view requested_key) {
    fs::path directory = FullPath(start_path);
    while (!directory.empty()) {
        const fs::path manifest = directory / L"autoenvplus.toml";
        if (FileExists(manifest)) {
            const std::wstring text = ReadUtf8File(manifest);
            std::istringstream input(WideToUtf8(text));
            std::string line;
            bool tools = false;
            std::unordered_map<std::string, std::string> values;
            while (std::getline(input, line)) {
                line = StripComment(line);
                Trim(line);
                if (line.empty()) continue;
                if (line.front() == '[' && line.back() == ']') {
                    std::string section = line.substr(1, line.size() - 2);
                    Trim(section);
                    tools = ToLowerAscii(section) == "tools";
                    continue;
                }
                if (!tools) continue;
                const size_t equals = line.find('=');
                if (equals == std::string::npos || equals == 0 || equals + 1 == line.size()) {
                    throw ShimError("Invalid [tools] entry in " + WideToUtf8(manifest.wstring()), 70);
                }
                std::string key = line.substr(0, equals);
                std::string value = line.substr(equals + 1);
                Trim(key); Trim(value);
                if (key.size() >= 2 && (key.front() == '\'' || key.front() == '"')
                    && key.back() == key.front()) {
                    key = key.substr(1, key.size() - 2);
                }
                key = NormalizeToolKey(std::move(key));
                if (key.empty()) {
                    throw ShimError("An unsupported tool is declared in " + WideToUtf8(manifest.wstring()), 70);
                }
                if (value.size() >= 2 && (value.front() == '\'' || value.front() == '"')
                    && value.back() == value.front()) {
                    value = value.substr(1, value.size() - 2);
                    Trim(value);
                } else if (value.find_first_of(" \t") != std::string::npos) {
                    throw ShimError("A project version selector contains unquoted whitespace.", 70);
                }
                if (value.empty()) {
                    throw ShimError("A project version selector is empty.", 70);
                }
                (void)Constraint::Parse(value);
                if (!values.emplace(key, value).second) {
                    throw ShimError("A project tool is declared more than once.", 70);
                }
            }
            auto found = values.find(std::string(requested_key));
            return found == values.end() ? std::nullopt
                                         : std::optional<std::string>(found->second);
        }
        const fs::path parent = directory.parent_path();
        if (parent == directory) break;
        directory = parent;
    }
    return std::nullopt;
}

std::optional<std::string> ReadGlobalSelector(
    const fs::path& managed_root,
    std::string_view requested_kind) {
    const fs::path profile_path = managed_root / L"state" / L"global-profile.json";
    if (!FileExists(profile_path)) return std::nullopt;
    const JsonObject root = ParseJsonFile(profile_path);
    if (RequiredInteger(root, L"schemaVersion") != 1) {
        throw ShimError("The global runtime profile schema is not supported.", 70);
    }
    if (!root.HasKey(L"selections")
        || root.GetNamedValue(L"selections").ValueType() != JsonValueType::Object) {
        throw ShimError("The global runtime profile selections property must be an object.", 70);
    }
    const JsonObject selections = root.GetNamedObject(L"selections");
    std::optional<std::string> result;
    for (const auto& pair : selections) {
        const std::string key = ToLowerAscii(WideToUtf8(pair.Key()));
        if (!KnownRuntimeKind(key)) {
            throw ShimError("The global runtime profile contains an unsupported runtime kind.", 70);
        }
        if (pair.Value().ValueType() != JsonValueType::String) {
            throw ShimError("A global runtime selection must be a string.", 70);
        }
        const std::string value = WideToUtf8(pair.Value().GetString());
        (void)Constraint::Parse(value);
        if (key == requested_kind) result = value;
    }
    return result;
}

struct SelectedInstallation {
    Installation installation;
    std::string scope;
};

SelectedInstallation SelectInstallation(
    const fs::path& managed_root,
    std::string_view runtime_kind,
    std::string_view project_key) {
    Constraint constraint;
    std::string scope;
    std::wstring session_name = runtime_kind == "nodejs"
        ? L"AUTOENVPLUS_NODE_VERSION"
        : L"AUTOENVPLUS_" + Utf8ToWide(std::string(runtime_kind)) + L"_VERSION";
    std::transform(session_name.begin(), session_name.end(), session_name.begin(), std::towupper);
    const std::wstring session = GetEnvironment(session_name);
    if (!session.empty()) {
        constraint = Constraint::Parse(WideToUtf8(session));
        scope = "Session";
    } else if (const auto project = ReadProjectSelector(
                   fs::current_path(), project_key)) {
        constraint = Constraint::Parse(*project);
        scope = "Project";
    } else if (const auto global = ReadGlobalSelector(managed_root, runtime_kind)) {
        constraint = Constraint::Parse(*global);
        scope = "Global";
    } else {
        constraint = Constraint::Parse("auto");
        scope = "Automatic";
    }
    std::vector<Installation> registry = LoadRegistry(managed_root);
    Installation* selected = nullptr;
    const std::string current_architecture = CurrentArchitecture();
    for (Installation& installation : registry) {
        if (installation.kind != runtime_kind
            || installation.architecture != current_architecture
            || !constraint.Matches(installation.version, installation.channels)) {
            continue;
        }
        if (selected == nullptr || installation.version.Compare(selected->version) > 0) {
            selected = &installation;
        }
    }
    if (selected == nullptr) {
        throw ShimError(
            "No managed " + std::string(runtime_kind) + " runtime matches '"
                + constraint.raw + "' from the " + scope + " scope.",
            69);
    }
    if (!FileExists(selected->executable)) {
        throw ShimError(
            "The selected runtime executable is missing: "
                + WideToUtf8(selected->executable.wstring()),
            66);
    }
    return SelectedInstallation{*selected, scope};
}

enum class LaunchMode { RuntimeExecutable, RelativeExecutable };

struct AliasDefinition {
    std::string runtime_kind;
    std::string project_key;
    LaunchMode mode;
    fs::path relative_tool;
    std::vector<std::wstring> prefix_arguments;
};

const std::unordered_map<std::wstring, AliasDefinition>& Aliases() {
    static const std::unordered_map<std::wstring, AliasDefinition> aliases = {
        {L"python", {"python", "python", LaunchMode::RuntimeExecutable, {}, {}}},
        {L"python3", {"python", "python", LaunchMode::RuntimeExecutable, {}, {}}},
        {L"pip", {"python", "python", LaunchMode::RuntimeExecutable, {}, {L"-m", L"pip"}}},
        {L"pip3", {"python", "python", LaunchMode::RuntimeExecutable, {}, {L"-m", L"pip"}}},
        {L"node", {"nodejs", "node", LaunchMode::RuntimeExecutable, {}, {}}},
        {L"npm", {"nodejs", "node", LaunchMode::RuntimeExecutable,
            L"node_modules\\npm\\bin\\npm-cli.js", {}}},
        {L"npx", {"nodejs", "node", LaunchMode::RuntimeExecutable,
            L"node_modules\\npm\\bin\\npx-cli.js", {}}},
        {L"java", {"java", "java", LaunchMode::RuntimeExecutable, {}, {}}},
        {L"javac", {"java", "java", LaunchMode::RelativeExecutable, L"bin\\javac.exe", {}}},
        {L"jar", {"java", "java", LaunchMode::RelativeExecutable, L"bin\\jar.exe", {}}},
        {L"dotnet", {"dotnet", "dotnet", LaunchMode::RuntimeExecutable, {}, {}}},
    };
    return aliases;
}

std::wstring QuoteArgument(std::wstring_view argument) {
    if (argument.empty()) return L"\"\"";
    if (argument.find_first_of(L" \t\n\v\"") == std::wstring_view::npos) {
        return std::wstring(argument);
    }
    std::wstring quoted = L"\"";
    size_t backslashes = 0;
    for (wchar_t character : argument) {
        if (character == L'\\') {
            ++backslashes;
        } else if (character == L'"') {
            quoted.append(backslashes * 2 + 1, L'\\');
            quoted.push_back(L'"');
            backslashes = 0;
        } else {
            quoted.append(backslashes, L'\\');
            backslashes = 0;
            quoted.push_back(character);
        }
    }
    quoted.append(backslashes * 2, L'\\');
    quoted.push_back(L'"');
    return quoted;
}

class EnvironmentOverride {
public:
    EnvironmentOverride(std::wstring name, std::wstring value)
        : name_(std::move(name)), old_(GetEnvironment(name_)), existed_(!old_.empty()) {
        if (!SetEnvironmentVariableW(name_.c_str(), value.c_str())) {
            throw ShimError("Unable to prepare the child process environment.", 70);
        }
    }
    ~EnvironmentOverride() {
        SetEnvironmentVariableW(name_.c_str(), existed_ ? old_.c_str() : nullptr);
    }
    EnvironmentOverride(const EnvironmentOverride&) = delete;
    EnvironmentOverride& operator=(const EnvironmentOverride&) = delete;

private:
    std::wstring name_;
    std::wstring old_;
    bool existed_;
};

int Launch(
    const fs::path& executable,
    const std::vector<std::wstring>& prefix,
    int argc,
    wchar_t** argv,
    const Installation& runtime,
    int depth) {
    std::wstring command_line = QuoteArgument(executable.wstring());
    for (const std::wstring& argument : prefix) {
        command_line += L" " + QuoteArgument(argument);
    }
    for (int index = 1; index < argc; ++index) {
        command_line += L" " + QuoteArgument(argv[index]);
    }
    std::wstring path_prefix;
    if (runtime.kind == "python") {
        path_prefix = runtime.root.wstring() + L";" + (runtime.root / L"Scripts").wstring();
    } else if (runtime.kind == "java") {
        path_prefix = (runtime.root / L"bin").wstring();
    } else {
        path_prefix = runtime.root.wstring();
    }
    const std::wstring inherited_path = GetEnvironment(L"PATH");
    EnvironmentOverride path_override(
        L"PATH", inherited_path.empty() ? path_prefix : path_prefix + L";" + inherited_path);
    EnvironmentOverride depth_override(L"AUTOENVPLUS_SHIM_DEPTH", std::to_wstring(depth));
    std::optional<EnvironmentOverride> java_home;
    if (runtime.kind == "java") {
        java_home.emplace(L"JAVA_HOME", runtime.root.wstring());
    }
    std::optional<EnvironmentOverride> dotnet_root;
    std::optional<EnvironmentOverride> dotnet_multilevel_lookup;
    if (runtime.kind == "dotnet") {
        dotnet_root.emplace(L"DOTNET_ROOT", runtime.root.wstring());
        dotnet_multilevel_lookup.emplace(L"DOTNET_MULTILEVEL_LOOKUP", L"0");
    }
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    std::vector<wchar_t> mutable_command(command_line.begin(), command_line.end());
    mutable_command.push_back(L'\0');
    if (!CreateProcessW(
            executable.c_str(), mutable_command.data(), nullptr, nullptr, TRUE, 0,
            nullptr, nullptr, &startup, &process)) {
        throw ShimError(
            "Unable to start the selected runtime (Win32 error "
                + std::to_string(GetLastError()) + "): "
                + WideToUtf8(executable.wstring()),
            71);
    }
    CloseHandle(process.hThread);
    SetConsoleCtrlHandler(nullptr, TRUE);
    const DWORD wait = WaitForSingleObject(process.hProcess, INFINITE);
    SetConsoleCtrlHandler(nullptr, FALSE);
    if (wait != WAIT_OBJECT_0) {
        CloseHandle(process.hProcess);
        throw ShimError("Waiting for the selected runtime failed.", 71);
    }
    DWORD exit_code = 1;
    if (!GetExitCodeProcess(process.hProcess, &exit_code)) {
        CloseHandle(process.hProcess);
        throw ShimError("Unable to read the selected runtime exit code.", 71);
    }
    CloseHandle(process.hProcess);
    return static_cast<int>(exit_code);
}

int ParseDepth() {
    const std::wstring text = GetEnvironment(L"AUTOENVPLUS_SHIM_DEPTH");
    if (text.empty()) return 0;
    try {
        return std::stoi(text);
    } catch (...) {
        return 0;
    }
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        const int depth = ParseDepth();
        if (depth >= kMaximumRecursionDepth) {
            throw ShimError("AutoEnvPlus Shim recursion limit reached.", 70);
        }
        const fs::path module = FullPath(GetModulePath());
        const std::wstring alias = ToLower(module.stem().wstring());
        const auto found = Aliases().find(alias);
        if (found == Aliases().end()) {
            throw ShimError("Unsupported AutoEnvPlus Shim name: " + WideToUtf8(alias), 64);
        }
        const fs::path shim_directory = module.parent_path();
        if (!EqualsIgnoreCase(shim_directory.filename().wstring(), L"shims")) {
            throw ShimError(
                "The native AutoEnvPlus Shim must run from the managed shims directory.", 65);
        }
        const fs::path managed_root = shim_directory.parent_path();
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        const SelectedInstallation selected = SelectInstallation(
            managed_root, found->second.runtime_kind, found->second.project_key);
        fs::path executable = selected.installation.executable;
        std::vector<std::wstring> prefix = found->second.prefix_arguments;
        if (!found->second.relative_tool.empty()) {
            const fs::path tool = ResolveInside(
                selected.installation.root,
                found->second.relative_tool,
                found->second.mode == LaunchMode::RelativeExecutable
                    ? "managed tool executable" : "managed tool script");
            if (!FileExists(tool)) {
                throw ShimError(
                    "The selected runtime does not contain "
                        + WideToUtf8(found->second.relative_tool.wstring()) + ".",
                    66);
            }
            if (found->second.mode == LaunchMode::RelativeExecutable) {
                executable = tool;
            } else {
                prefix.insert(prefix.begin(), tool.wstring());
            }
        }
        if (GetEnvironment(L"AUTOENVPLUS_SHIM_TRACE") == L"1") {
            WriteError(
                "AutoEnvPlus Shim: " + WideToUtf8(alias) + " -> "
                + WideToUtf8(executable.wstring()) + " ["
                + selected.installation.version.ToString() + "] ("
                + selected.scope + ")");
        }
        return Launch(
            executable, prefix, argc, argv,
            selected.installation, depth + 1);
    } catch (const ShimError& error) {
        WriteError(error.what());
        return error.exit_code;
    } catch (const winrt::hresult_error& error) {
        WriteError("AutoEnvPlus Shim WinRT error: " + WideToUtf8(error.message()));
        return 70;
    } catch (const std::exception& error) {
        WriteError(std::string("AutoEnvPlus Shim: ") + error.what());
        return 70;
    }
}
