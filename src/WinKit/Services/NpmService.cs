using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinKit.Services;

public sealed record NpmPackageInfo(string Name, string Version);

public sealed record NpmEnvironmentInfo(
    bool IsAvailable,
    string NodePath,
    string NodeVersion,
    string NpmPath,
    string NpmVersion,
    string Prefix,
    string Error);

public sealed record NpmLoadResult(
    NpmEnvironmentInfo Environment,
    IReadOnlyList<NpmPackageInfo> Packages,
    string Message);

public sealed record NpmUpdateCheckResult(
    bool Success,
    IReadOnlyDictionary<string, string> LatestVersions,
    string Message);

public sealed record NpmOperationResult(bool Success, string Message);

internal sealed record NpmExecutables(string NodePath, string NpmPath, string NpmCliPath);

internal sealed record NpmProcessOutput(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    string StartError = "",
    bool TerminationRequested = false);

internal interface INpmCommandRunner
{
    Task<NpmProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class NpmService
{
    private static readonly Regex SemVerPattern = new(
        @"^[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TagPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(5);

    private readonly INpmCommandRunner _runner;
    private readonly Func<NpmExecutables?> _discoverExecutables;
    private readonly SemaphoreSlim _environmentLock = new(1, 1);
    private NpmEnvironmentInfo? _cachedEnvironment;
    private NpmExecutables? _cachedExecutables;

    public NpmService() : this(new NpmCommandRunner(), DiscoverExecutables)
    {
    }

    internal NpmService(INpmCommandRunner runner, Func<NpmExecutables?> discoverExecutables)
    {
        _runner = runner;
        _discoverExecutables = discoverExecutables;
    }

    public async Task<NpmLoadResult> LoadAsync(
        bool refreshEnvironment = false,
        CancellationToken cancellationToken = default)
    {
        var environment = await GetEnvironmentAsync(refreshEnvironment, cancellationToken);
        if (!environment.IsAvailable || _cachedExecutables is null)
            return new NpmLoadResult(environment, Array.Empty<NpmPackageInfo>(), environment.Error);

        var result = await RunNpmAsync(
            ["ls", "--global", "--depth=0", "--json"], ReadTimeout, cancellationToken);
        if (!TryParseGlobalPackages(result.StandardOutput, out var packages, out var parseError))
        {
            var error = GetFailureMessage("读取全局包列表失败", result);
            if (!string.IsNullOrWhiteSpace(parseError)) error += " " + parseError;
            return new NpmLoadResult(environment, Array.Empty<NpmPackageInfo>(), error);
        }

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(environment.Error)) warnings.Add(environment.Error);
        if (!string.IsNullOrWhiteSpace(result.StartError)) warnings.Add("npm ls 启动失败：" + result.StartError);
        if (result.TimedOut) warnings.Add("npm ls 超时；以下内容可能只是部分本机清单。");
        if (result.StandardOutputTruncated || result.StandardErrorTruncated)
            warnings.Add("npm ls 输出已达到上限；以下内容可能不完整。");
        if (result.ExitCode != 0)
            warnings.Add("npm ls 返回异常状态；仍显示可解析的本机包，详情：" + GetFailureMessage("npm ls", result));
        return new NpmLoadResult(environment, packages, string.Join(" ", warnings));
    }

    public async Task<NpmUpdateCheckResult> CheckUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var environment = await GetEnvironmentAsync(false, cancellationToken);
        if (!environment.IsAvailable || _cachedExecutables is null)
            return new NpmUpdateCheckResult(false, new Dictionary<string, string>(), environment.Error);

        var result = await RunNpmAsync(["outdated", "--global", "--json"], ReadTimeout, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.StartError) || result.TimedOut
            || result.StandardOutputTruncated || result.StandardErrorTruncated
            || result.ExitCode is not (0 or 1)
            || HasNpmErrorOutput(result.StandardError))
        {
            return new NpmUpdateCheckResult(false, new Dictionary<string, string>(),
                GetFailureMessage("检查 npm 更新失败", result));
        }

        if (!TryParseOutdated(result.StandardOutput, out var latestVersions, out var parseError))
        {
            var error = GetFailureMessage("检查 npm 更新失败", result);
            if (!string.IsNullOrWhiteSpace(parseError)) error += " " + parseError;
            return new NpmUpdateCheckResult(false, latestVersions, error);
        }

        if (result.ExitCode == 1 && latestVersions.Count == 0)
            return new NpmUpdateCheckResult(false, latestVersions,
                "npm outdated 返回退出码 1，但没有提供任何可解析的过期包；未将此结果视为全部最新。 "
                + GetFailureMessage("检查 npm 更新失败", result));

        return new NpmUpdateCheckResult(true, latestVersions, string.Empty);
    }

    public async Task<NpmOperationResult> InstallAsync(
        string packageName,
        string? versionOrTag,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildPackageTarget(packageName, versionOrTag, out var target, out var validationError))
            return new NpmOperationResult(false, validationError);

        return await RunMutationAsync(
            ["install", "--global", "--no-audit", "--no-fund", "--progress=false", "--", target],
            "安装全局包失败",
            cancellationToken);
    }

    public async Task<NpmOperationResult> UpdateAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidatePackageName(packageName, out var validationError))
            return new NpmOperationResult(false, validationError);

        return await RunMutationAsync(
            ["install", "--global", "--no-audit", "--no-fund", "--progress=false", "--", packageName + "@latest"],
            "更新全局包失败",
            cancellationToken);
    }

    public async Task<NpmOperationResult> UninstallAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidatePackageName(packageName, out var validationError))
            return new NpmOperationResult(false, validationError);

        return await RunMutationAsync(
            ["uninstall", "--global", "--no-audit", "--no-fund", "--progress=false", "--", packageName],
            "卸载全局包失败",
            cancellationToken);
    }

    public static bool TryValidatePackageName(string? packageName, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(packageName) || packageName.Length > 214
            || !string.Equals(packageName, packageName.Trim(), StringComparison.Ordinal))
        {
            error = "请输入有效的 npm 包名。";
            return false;
        }

        string[] parts;
        if (packageName[0] == '@')
        {
            var slash = packageName.IndexOf('/');
            if (slash <= 1 || slash == packageName.Length - 1 || packageName.IndexOf('/', slash + 1) >= 0)
            {
                error = "作用域包名必须采用 @scope/package 格式。";
                return false;
            }

            parts = [packageName[1..slash], packageName[(slash + 1)..]];
        }
        else
        {
            if (packageName.Contains('/'))
            {
                error = "只允许 npm 包名；不接受本地路径或 Git/URL 地址。";
                return false;
            }

            parts = [packageName];
        }

        if (parts.Any(part => !IsValidNamePart(part)))
        {
            error = "包名仅支持小写字母、数字、点、连字符和下划线，且各部分不能以点、下划线或连字符开头。";
            return false;
        }

        return true;
    }

    public static bool TryValidateVersionOrTag(string? versionOrTag, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(versionOrTag)) return true;
        if (!string.Equals(versionOrTag, versionOrTag.Trim(), StringComparison.Ordinal)
            || versionOrTag.Length > 128)
        {
            error = "版本或 dist-tag 格式无效。";
            return false;
        }

        if (TryParseSemVer(versionOrTag, out _) || TagPattern.IsMatch(versionOrTag)) return true;
        error = "仅支持完整 semver 版本（如 2.3.4-beta.1）或简单 dist-tag（如 next）；不支持范围、URL、Git 或路径。";
        return false;
    }

    public static bool TryBuildPackageTarget(
        string? packageName,
        string? versionOrTag,
        out string target,
        out string error)
    {
        target = "";
        if (!TryValidatePackageName(packageName, out error)) return false;
        if (!TryValidateVersionOrTag(versionOrTag, out error)) return false;
        target = string.IsNullOrWhiteSpace(versionOrTag) ? packageName! : packageName + "@" + versionOrTag;
        return true;
    }

    public static int? CompareSemVer(string? left, string? right)
    {
        if (!TryParseSemVer(left, out var leftVersion) || !TryParseSemVer(right, out var rightVersion)) return null;
        var compare = leftVersion.Major.CompareTo(rightVersion.Major);
        if (compare != 0) return compare;
        compare = leftVersion.Minor.CompareTo(rightVersion.Minor);
        if (compare != 0) return compare;
        compare = leftVersion.Patch.CompareTo(rightVersion.Patch);
        if (compare != 0) return compare;
        return ComparePrerelease(leftVersion.Prerelease, rightVersion.Prerelease);
    }

    internal static bool TryParseGlobalPackages(
        string json,
        out IReadOnlyList<NpmPackageInfo> packages,
        out string error)
    {
        var parsed = new List<NpmPackageInfo>();
        packages = parsed;
        error = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                error = "npm ls 输出缺少 dependencies 对象。";
                return false;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (!TryValidatePackageName(dependency.Name, out _)
                    || dependency.Value.ValueKind != JsonValueKind.Object
                    || !dependency.Value.TryGetProperty("version", out var version)
                    || version.ValueKind != JsonValueKind.String)
                    continue;

                var value = version.GetString();
                if (!string.IsNullOrWhiteSpace(value)) parsed.Add(new NpmPackageInfo(dependency.Name, value));
            }

            packages = parsed.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return true;
        }
        catch (JsonException ex)
        {
            error = "npm ls 返回的 JSON 无法解析：" + ex.Message;
            return false;
        }
    }

    internal static bool TryParseOutdated(
        string json,
        out IReadOnlyDictionary<string, string> latestVersions,
        out string error)
    {
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        latestVersions = latest;
        error = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "npm outdated 输出不是 JSON 对象。";
                return false;
            }

            if (root.TryGetProperty("error", out var rootError))
            {
                error = GetJsonError(rootError);
                return false;
            }

            foreach (var item in root.EnumerateObject())
            {
                if (!TryValidatePackageName(item.Name, out _) || item.Value.ValueKind != JsonValueKind.Object) continue;
                if (item.Value.TryGetProperty("error", out var itemError))
                {
                    error = $"{item.Name}: {GetJsonError(itemError)}";
                    return false;
                }

                if (item.Value.TryGetProperty("latest", out var version)
                    && version.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(version.GetString()))
                    latest[item.Name] = version.GetString()!;
            }

            latestVersions = latest;
            return true;
        }
        catch (JsonException ex)
        {
            error = "npm outdated 返回的 JSON 无法解析：" + ex.Message;
            return false;
        }
    }

    private async Task<NpmOperationResult> RunMutationAsync(
        IReadOnlyList<string> arguments,
        string failurePrefix,
        CancellationToken cancellationToken)
    {
        var environment = await GetEnvironmentAsync(false, cancellationToken);
        if (!environment.IsAvailable || _cachedExecutables is null)
            return new NpmOperationResult(false, environment.Error);

        var result = await RunNpmAsync(arguments, MutationTimeout, cancellationToken);
        if (result.TimedOut || result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StartError))
            return new NpmOperationResult(false, GetFailureMessage(failurePrefix, result));

        var output = JoinOutput(result);
        if (result.StandardOutputTruncated || result.StandardErrorTruncated)
            output = string.IsNullOrWhiteSpace(output)
                ? "npm 操作已完成，但命令输出已截断。"
                : output + Environment.NewLine + "（命令输出已截断，仅显示部分信息。）";
        return new NpmOperationResult(true,
            string.IsNullOrWhiteSpace(output) ? "npm 操作已完成。" : output);
    }

    private async Task<NpmEnvironmentInfo> GetEnvironmentAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _cachedEnvironment is not null) return _cachedEnvironment;
        await _environmentLock.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cachedEnvironment is not null) return _cachedEnvironment;

            _cachedEnvironment = null;
            _cachedExecutables = null;
            NpmExecutables? executables;
            try
            {
                executables = _discoverExecutables();
            }
            catch (Exception ex)
            {
                return _cachedEnvironment = Unavailable("识别 Node/npm 环境失败：" + ex.Message);
            }

            if (executables is null)
                return _cachedEnvironment = Unavailable("未找到 PATH 中可用的 Node.js 与 npm CLI。请在安装后刷新环境。");

            var nodeVersion = await _runner.RunAsync(executables.NodePath, ["--version"], ProbeTimeout, cancellationToken);
            var npmVersion = await _runner.RunAsync(
                executables.NodePath, [executables.NpmCliPath, "--version"], ProbeTimeout, cancellationToken);
            var prefix = await _runner.RunAsync(
                executables.NodePath, [executables.NpmCliPath, "config", "get", "prefix"], ProbeTimeout, cancellationToken);

            var nodeVersionText = nodeVersion.StandardOutput.Trim();
            var npmVersionText = npmVersion.StandardOutput.Trim();
            var prefixText = prefix.StandardOutput.Trim();
            if (nodeVersion.ExitCode != 0 || npmVersion.ExitCode != 0
                || nodeVersion.TimedOut || npmVersion.TimedOut
                || !string.IsNullOrWhiteSpace(nodeVersion.StartError)
                || !string.IsNullOrWhiteSpace(npmVersion.StartError)
                || string.IsNullOrWhiteSpace(nodeVersionText)
                || string.IsNullOrWhiteSpace(npmVersionText))
            {
                var details = new[]
                {
                    nodeVersion.ExitCode == 0 ? "" : GetFailureMessage("node --version", nodeVersion),
                    npmVersion.ExitCode == 0 ? "" : GetFailureMessage("npm --version", npmVersion)
                }.Where(s => !string.IsNullOrWhiteSpace(s));
                return _cachedEnvironment = Unavailable("找到 npm 文件，但无法执行 Node/npm：" + string.Join(" ", details));
            }

            var prefixError = prefix.ExitCode == 0 && !prefix.TimedOut ? "" : GetFailureMessage("读取 npm prefix 失败", prefix);
            _cachedExecutables = executables;
            return _cachedEnvironment = new NpmEnvironmentInfo(
                true,
                executables.NodePath,
                nodeVersionText,
                executables.NpmPath,
                npmVersionText,
                string.IsNullOrWhiteSpace(prefixText) ? "未知" : prefixText,
                prefixError);
        }
        finally
        {
            _environmentLock.Release();
        }
    }

    private Task<NpmProcessOutput> RunNpmAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var executables = _cachedExecutables
            ?? throw new InvalidOperationException("npm environment has not been resolved");
        var cliArguments = new string[arguments.Count + 1];
        cliArguments[0] = executables.NpmCliPath;
        for (var i = 0; i < arguments.Count; i++) cliArguments[i + 1] = arguments[i];
        return _runner.RunAsync(executables.NodePath, cliArguments, timeout, cancellationToken);
    }

    private static NpmEnvironmentInfo Unavailable(string error)
        => new(false, "", "", "", "", "", error);

    private static NpmExecutables? DiscoverExecutables()
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"'))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var nodePath = pathDirectories
            .Select(path => Path.Combine(path, "node.exe"))
            .FirstOrDefault(File.Exists);
        var npmPath = pathDirectories
            .Select(path => Path.Combine(path, "npm.cmd"))
            .FirstOrDefault(File.Exists);

        string? npmCliPath = null;
        if (npmPath is not null)
        {
            var npmDirectory = Path.GetDirectoryName(npmPath)!;
            npmCliPath = GetNpmCliPath(npmDirectory);
            var colocatedNode = Path.Combine(npmDirectory, "node.exe");
            if (npmCliPath is not null && File.Exists(colocatedNode)) nodePath = colocatedNode;
        }

        if (npmCliPath is null && nodePath is not null)
        {
            var nodeDirectory = Path.GetDirectoryName(nodePath)!;
            npmCliPath = GetNpmCliPath(nodeDirectory);
            if (npmCliPath is not null && npmPath is null)
                npmPath = Path.Combine(nodeDirectory, "npm.cmd");
        }

        if (nodePath is null || npmCliPath is null) return null;
        return new NpmExecutables(nodePath, npmPath ?? npmCliPath, npmCliPath);
    }

    private static string? GetNpmCliPath(string directory)
    {
        var path = Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js");
        return File.Exists(path) ? path : null;
    }

    private static bool IsValidNamePart(string part)
    {
        if (part.Length == 0 || part[0] is '.' or '_' or '-') return false;
        return part.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
    }

    private static bool TryParseSemVer(string? value, out ParsedSemVer parsed)
    {
        parsed = new ParsedSemVer(BigInteger.Zero, BigInteger.Zero, BigInteger.Zero, Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = SemVerPattern.Match(value);
        if (!match.Success) return false;
        if (!BigInteger.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !BigInteger.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !BigInteger.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;

        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        foreach (var identifier in prerelease)
        {
            if (identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit)) return false;
        }

        parsed = new ParsedSemVer(major, minor, patch, prerelease);
        return true;
    }

    private static int ComparePrerelease(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return left.Count == right.Count ? 0 : left.Count == 0 ? 1 : -1;

        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var leftNumeric = left[i].All(char.IsAsciiDigit);
            var rightNumeric = right[i].All(char.IsAsciiDigit);
            int compare;
            if (leftNumeric && rightNumeric)
            {
                var leftNumber = BigInteger.Parse(left[i], CultureInfo.InvariantCulture);
                var rightNumber = BigInteger.Parse(right[i], CultureInfo.InvariantCulture);
                compare = leftNumber.CompareTo(rightNumber);
            }
            else if (leftNumeric != rightNumeric)
            {
                compare = leftNumeric ? -1 : 1;
            }
            else
            {
                compare = string.CompareOrdinal(left[i], right[i]);
            }

            if (compare != 0) return compare;
        }

        return left.Count.CompareTo(right.Count);
    }

    private static bool HasNpmErrorOutput(string standardError)
        => Regex.IsMatch(standardError, @"(?im)^\s*npm\s+(?:error|ERR!)(?:\s|$)", RegexOptions.CultureInvariant);

    private static string GetJsonError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "summary", "detail", "message", "code" })
            {
                if (error.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!;
            }
        }

        return error.ValueKind == JsonValueKind.String ? error.GetString() ?? "npm registry 返回错误。" : "npm registry 返回错误。";
    }

    private static string GetFailureMessage(string prefix, NpmProcessOutput result)
    {
        var details = JoinOutput(result);
        var status = result.TimedOut
            ? result.TerminationRequested
                ? "命令超时；已向进程树提交终止请求，但不能保证所有子进程均已退出。"
                : "命令超时；进程树未确认终止，输出收尾等待已限制时长。"
            : !string.IsNullOrWhiteSpace(result.StartError) ? result.StartError
            : $"进程退出码 {result.ExitCode}。";
        var truncated = result.StandardOutputTruncated || result.StandardErrorTruncated ? "（输出已截断）" : "";
        return string.IsNullOrWhiteSpace(details)
            ? $"{prefix}：{status}{truncated}"
            : $"{prefix}：{status}{truncated} {details}";
    }

    private static string JoinOutput(NpmProcessOutput result)
    {
        var stdout = result.StandardOutput.Trim();
        var stderr = result.StandardError.Trim();
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout
            : string.IsNullOrWhiteSpace(stdout) ? stderr
            : stdout + Environment.NewLine + stderr;
        const int detailLimit = 4000;
        return combined.Length <= detailLimit ? combined : combined[..detailLimit] + "…";
    }

    private sealed record ParsedSemVer(BigInteger Major, BigInteger Minor, BigInteger Patch, string[] Prerelease);
}

internal sealed class NpmCommandRunner : INpmCommandRunner
{
    private const int OutputLimit = 512 * 1024;
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(2);

    public async Task<NpmProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";
        startInfo.Environment["NPM_CONFIG_PROGRESS"] = "false";
        startInfo.Environment["NPM_CONFIG_COLOR"] = "false";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return new NpmProcessOutput(-1, "", "", false, false, false, "无法启动进程。");
        }
        catch (Exception ex)
        {
            return new NpmProcessOutput(-1, "", "", false, false, false, ex.Message);
        }

        var stdoutCapture = new BoundedOutput(OutputLimit);
        var stderrCapture = new BoundedOutput(OutputLimit);
        using var drainCancellation = new CancellationTokenSource();
        var stdoutTask = DrainAsync(process.StandardOutput, stdoutCapture, drainCancellation.Token);
        var stderrTask = DrainAsync(process.StandardError, stderrCapture, drainCancellation.Token);
        var processExitTask = process.WaitForExitAsync();
        var drainTask = Task.WhenAll(stdoutTask, stderrTask);
        var completionTask = Task.WhenAll(processExitTask, stdoutTask, stderrTask);
        var timedOut = false;
        var terminationRequested = false;
        try
        {
            await completionTask.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            timedOut = true;
            terminationRequested = TryKill(process);
            await WaitBoundedAsync(processExitTask, CleanupWait);
            drainCancellation.Cancel();
            await WaitBoundedAsync(drainTask, CleanupWait);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitBoundedAsync(processExitTask, CleanupWait);
            drainCancellation.Cancel();
            await WaitBoundedAsync(drainTask, CleanupWait);
            throw;
        }

        var exitCode = -1;
        try
        {
            if (process.HasExited) exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        var stdout = stdoutCapture.Snapshot();
        var stderr = stderrCapture.Snapshot();
        return new NpmProcessOutput(exitCode, stdout.Text, stderr.Text, timedOut,
            stdout.Truncated, stderr.Truncated, TerminationRequested: terminationRequested);
    }

    private static async Task DrainAsync(StreamReader reader, BoundedOutput capture, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                capture.Append(buffer, read);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task WaitBoundedAsync(Task task, TimeSpan timeout)
    {
        try { await task.WaitAsync(timeout); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (process.HasExited) return false;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class BoundedOutput
    {
        private readonly int _limit;
        private readonly StringBuilder _builder = new();
        private readonly object _sync = new();
        private bool _truncated;

        public BoundedOutput(int limit) => _limit = limit;

        public void Append(char[] buffer, int count)
        {
            lock (_sync)
            {
                var remaining = _limit - _builder.Length;
                if (remaining > 0) _builder.Append(buffer, 0, Math.Min(remaining, count));
                if (count > remaining) _truncated = true;
            }
        }

        public (string Text, bool Truncated) Snapshot()
        {
            lock (_sync) return (_builder.ToString(), _truncated);
        }
    }
}
