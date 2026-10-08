using System.Text.RegularExpressions;

namespace AutoEnvPlus.App.Text;

/// <summary>
/// Maps the most safety-relevant English error messages produced by the core
/// layer to Chinese for display. Core messages stay English because the core
/// test suite asserts on their exact text; this translator runs at the UI
/// boundary only. Unknown messages pass through unchanged.
/// </summary>
public static class CoreErrorText
{
    private sealed record Rule(Regex Pattern, string Chinese);

    private static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant);

    private static readonly Rule[] Rules =
    [
        // Integrity failures - the most safety-critical messages a user can see.
        new(
            R(@"^(SHA-256|SHA-512) mismatch for '(.+?)'\. Expected ([0-9a-f]+), but received ([0-9a-f]+)\.$"),
            "$1 校验不匹配（文件 $2）：预期 $3，实际 $4。内容未提交到受管目录。"),
        new(
            R(@"^The archive does not contain '(.+?)' in its payload root\.$"),
            "压缩包根目录中没有预期的可执行文件“$1”；包内容与清单声明不符。"),
        new(
            R(@"^The completed detached signature verification does not match the install plan\.$"),
            "安装完成后的签名复核与安装计划不一致；内容可能在安装过程中被篡改。"),
        new(
            R(@"^The assembled download length does not match the probed entity length\.$"),
            "下载完成后的总长度与探测到的长度不一致；远端内容可能在传输中发生了变化。"),
        new(
            R(@"^Segment (\d+) returned a mismatched Content-Range value\.$"),
            "第 $1 段返回的 Content-Range 与请求范围不一致；远端内容可能已变化。"),
        new(
            R(@"^The range probe omitted Content-Range\.$"),
            "服务器未返回 Content-Range，无法进行分段下载。"),
        new(
            R(@"^Segmented transfers require a known content length\.$"),
            "分段下载需要已知的内容长度；服务器未提供。"),
        new(
            R(@"^Segmented transfers require a stable entity identity\.$"),
            "分段下载需要稳定的内容标识；服务器响应不支持。"),
        new(
            R(@"^Expected hash must be a valid (SHA-256|SHA-512) value\.$"),
            "预期摘要必须是有效的 $1 十六进制值。"),

        // Download library destination rules.
        new(
            R(@"^The destination '(.+?)' already exists\.$"),
            "目标文件“$1”已存在；请先处理同名文件或选择覆盖。"),
        new(
            R(@"^The destination must be a direct child of the managed download library\.$"),
            "目标必须是受管下载库的直接子文件。"),
        new(
            R(@"^The destination must use an approved package or archive extension\.$"),
            "目标文件扩展名必须是受支持的包或压缩包格式。"),
        new(
            R(@"^The destination is not a regular file\.$"),
            "目标不是普通文件（可能是目录或设备）。"),
        new(
            R(@"^The destination is a reparse point and cannot be replaced\.$"),
            "目标是重解析点，不能替换。"),
        new(
            R(@"^Could not allocate a unique transfer staging directory\.$"),
            "无法分配唯一的传输暂存目录。"),
        new(
            R(@"^The download library manifest is empty\.$"),
            "下载库清单为空。"),
        new(
            R(@"^The download library manifest is invalid JSON\.$"),
            "下载库清单不是有效的 JSON。"),
        new(
            R(@"^Connection count must be one of 1, 2, 4, 8, or 16\.$"),
            "连接数必须是 1、2、4、8 或 16。"),

        // pip local package install.
        new(
            R(@"^The wheel must be a top-level file in the managed downloads library\.$"),
            "wheel 文件必须位于受管下载库顶层。"),
        new(
            R(@"^Environment names must use 1-64 ASCII letters, digits, periods, underscores, or hyphens, with an alphanumeric first and last character\.$"),
            "环境名称必须使用 1-64 个 ASCII 字母、数字、点、下划线或连字符，且首尾为字母或数字。"),

        // Project / CMake / storage.
        new(
            R(@"^Project directory does not exist: (.+)$"),
            "项目目录不存在：$1"),
        new(
            R(@"^The generated CMake configure preset does not match the AutoEnvPlus template\.$"),
            "CMake 配置预设与 AutoEnvPlus 模板不一致；文件可能在写入后被修改。"),
        new(
            R(@"^Cache source does not exist: (.+)$"),
            "缓存源目录不存在：$1"),
        new(
            R(@"^Cache directory does not exist: (.+)$"),
            "缓存目录不存在：$1"),
        new(
            R(@"^Cache cleanup cannot target an isolation directory\.$"),
            "缓存清理不能以隔离目录为目标。"),
        new(
            R(@"^Cache migration does not follow reparse points or device entries: (.+)$"),
            "缓存迁移不跟随重解析点或设备项：$1"),
        new(
            R(@"^Cache migration does not follow reparse points: (.+)$"),
            "缓存迁移不跟随重解析点：$1"),

        // Session resolution.
        new(
            R(@"^The pinned (.+?) runtime does not match the requested kind, architecture, active version selector, or Provider\. Refresh the session selection\.$"),
            "固定的 $1 运行时与请求的类型、架构、版本选择器或 Provider 不一致；请刷新会话选择。"),

        // PowerShell integration.
        new(
            R(@"^The PowerShell Profile preview does not match its original bytes\.$"),
            "PowerShell Profile 预览复核失败；文件可能在预览后发生了变化。"),
        new(
            R(@"^The PowerShell module preview does not match its original bytes\.$"),
            "PowerShell 模块预览复核失败；文件可能在预览后发生了变化。"),
    ];

    public static string Localize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        foreach (Rule rule in Rules)
        {
            if (rule.Pattern.Match(message) is { Success: true } match)
            {
                return match.Result(rule.Chinese);
            }
        }

        return message;
    }
}
