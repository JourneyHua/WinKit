using WinKit.Services;

internal static class Program
{
    private static int _assertions;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child-probe")
            return RunChildProbe(args.Skip(1).ToArray());

        ValidateTargetsAndSemVer();
        ParseFixtures();
        await CheckUpdateOutcomesAsync();
        await PreservePartialLocalListAsync();
        await VerifySafeCommandArgumentsAsync();
        await VerifyProcessRunnerAsync();
        Console.WriteLine($"NpmProbe passed: {_assertions} assertions.");
        return 0;
    }

    private static void ValidateTargetsAndSemVer()
    {
        Check(NpmService.TryBuildPackageTarget("left-pad", null, out var plain, out _)
              && plain == "left-pad", "plain package name accepted");
        Check(NpmService.TryBuildPackageTarget("@angular/cli", "18.2.0-beta.1", out var scoped, out _)
              && scoped == "@angular/cli@18.2.0-beta.1", "scoped semver target");
        Check(NpmService.TryBuildPackageTarget("@scope/pkg", "next", out var tag, out _)
              && tag == "@scope/pkg@next", "scoped dist-tag target");

        foreach (var invalid in new[]
                 {
                     "UPPER", "@/pkg", "@scope/", "a/b", "https://example.test/pkg.tgz",
                     "git+https://example.test/repo.git", "../outside", "foo bar", "--help"
                 })
            Check(!NpmService.TryValidatePackageName(invalid, out _), $"reject package name: {invalid}");

        foreach (var invalid in new[] { "^1.2.3", "latest;whoami", "../tag", "https://example.test/pkg" })
            Check(!NpmService.TryValidateVersionOrTag(invalid, out _), $"reject version/tag: {invalid}");

        Check(NpmService.CompareSemVer("1.0.0", "1.0.0-rc.1") > 0, "release follows prerelease");
        Check(NpmService.CompareSemVer("1.0.0-rc.10", "1.0.0-rc.2") > 0, "numeric prerelease comparison");
        Check(NpmService.CompareSemVer("2.0.0", "1.99.99") > 0, "major comparison");
        Check(NpmService.CompareSemVer("1.2.3", "not-semver") is null, "invalid semver is indeterminate");
    }

    private static void ParseFixtures()
    {
        const string installedFixture = """
            {"dependencies":{"alpha":{"version":"1.0.0"},"@scope/pkg":{"version":"2.1.0"},"broken":{},"child":{"version":"1.0.0","dependencies":{"nested":{"version":"9.0.0"}}}}}
            """;
        Check(NpmService.TryParseGlobalPackages(installedFixture, out var packages, out _), "parse global package fixture");
        Check(packages.Select(p => p.Name).SequenceEqual(new[] { "@scope/pkg", "alpha", "child" }),
            "read direct packages and ignore nested/missing versions");
        Check(!NpmService.TryParseGlobalPackages("{}", out _, out _), "reject missing dependencies fixture");

        const string outdatedFixture = """
            {"@scope/pkg":{"current":"2.1.0","latest":"3.0.0"},"alpha":{"current":"1.0.0","latest":"1.1.0"}}
            """;
        Check(NpmService.TryParseOutdated(outdatedFixture, out var outdated, out _), "parse outdated fixture");
        Check(outdated.Count == 2 && outdated["@scope/pkg"] == "3.0.0", "retain scoped latest version");
    }

    private static async Task CheckUpdateOutcomesAsync()
    {
        var good = CreateService(new NpmProcessOutput(1,
            """{"alpha":{"current":"1.0.0","latest":"2.0.0"}}""", "", false, false, false));
        var goodResult = await good.Service.CheckUpdatesAsync();
        Check(goodResult.Success && goodResult.LatestVersions["alpha"] == "2.0.0",
            "outdated exit 1 with update is success");

        var noUpdates = CreateService(new NpmProcessOutput(0, "{}", "", false, false, false));
        Check((await noUpdates.Service.CheckUpdatesAsync()).Success, "exit 0 empty JSON means no updates");

        var failedEmpty = CreateService(new NpmProcessOutput(1, "{}", "", false, false, false));
        Check(!(await failedEmpty.Service.CheckUpdatesAsync()).Success, "exit 1 empty JSON is not success");

        var badExit = CreateService(new NpmProcessOutput(2, "{}", "unexpected", false, false, false));
        Check(!(await badExit.Service.CheckUpdatesAsync()).Success, "reject unexpected exit code");

        var timeout = CreateService(new NpmProcessOutput(1,
            """{"alpha":{"latest":"2.0.0"}}""", "", true, false, false));
        Check(!(await timeout.Service.CheckUpdatesAsync()).Success, "reject timed out registry check");

        var stdoutTruncated = CreateService(new NpmProcessOutput(1,
            "{}", "", false, true, false));
        Check(!(await stdoutTruncated.Service.CheckUpdatesAsync()).Success, "reject truncated registry JSON");

        var stderrTruncated = CreateService(new NpmProcessOutput(0,
            "{}", "", false, false, true));
        Check(!(await stderrTruncated.Service.CheckUpdatesAsync()).Success, "reject truncated stderr");

        var startError = CreateService(new NpmProcessOutput(-1,
            "{}", "", false, false, false, "fixture start failure"));
        Check(!(await startError.Service.CheckUpdatesAsync()).Success, "reject process start failure");

        var npmError = CreateService(new NpmProcessOutput(1,
            "{}", "npm error code E401", false, false, false));
        Check(!(await npmError.Service.CheckUpdatesAsync()).Success, "reject npm registry error on stderr");
    }

    private static async Task PreservePartialLocalListAsync()
    {
        const string fixture = """{"dependencies":{"alpha":{"version":"1.0.0"}}}""";
        var result = new NpmProcessOutput(1, fixture, "npm ls returned non-zero", true, false, false);
        var harness = CreateService(new NpmProcessOutput(0, "{}", "", false, false, false), result);
        var snapshot = await harness.Service.LoadAsync();
        Check(snapshot.Packages.Count == 1 && snapshot.Packages[0].Name == "alpha",
            "preserve parseable local package output after timeout");
        Check(snapshot.Message.Contains("超时", StringComparison.Ordinal)
              && snapshot.Message.Contains("部分", StringComparison.Ordinal), "mark local list incomplete");
    }

    private static async Task VerifySafeCommandArgumentsAsync()
    {
        var harness = CreateService(new NpmProcessOutput(0, "", "", false, false, false));
        var rejected = await harness.Service.InstallAsync("valid-name", "latest; calc.exe");
        Check(!rejected.Success && harness.Runner.MutationCalls.Count == 0,
            "reject command metacharacters before spawning");

        var installed = await harness.Service.InstallAsync("@scope/pkg", "next");
        Check(installed.Success, "fake install result accepted");
        var updated = await harness.Service.UpdateAsync("@scope/pkg");
        Check(updated.Success, "fake single-package update result accepted");
        var removed = await harness.Service.UninstallAsync("@scope/pkg");
        Check(removed.Success, "fake uninstall result accepted");

        var commands = harness.Runner.MutationCalls
            .Select(call => call.Arguments.Skip(1).ToArray()).ToArray();
        Check(commands.Length == 3, "only three injected fake mutation commands executed");
        Check(commands[0].SequenceEqual(new[]
            { "install", "--global", "--no-audit", "--no-fund", "--progress=false", "--", "@scope/pkg@next" }),
            "install target passed as one argument after option terminator");
        Check(commands[1].SequenceEqual(new[]
            { "install", "--global", "--no-audit", "--no-fund", "--progress=false", "--", "@scope/pkg@latest" }),
            "update scopes latest to exactly one package");
        Check(commands[2].SequenceEqual(new[]
            { "uninstall", "--global", "--no-audit", "--no-fund", "--progress=false", "--", "@scope/pkg" }),
            "uninstall target passed as one argument after option terminator");
        Check(harness.Runner.Calls.All(call => call.Executable == @"C:\fake\node.exe"),
            "npm CLI is invoked through the discovered Node executable");
        Check(harness.Runner.MutationCalls.All(call =>
                call.Arguments[0] == @"C:\fake\node_modules\npm\bin\npm-cli.js"),
            "npm CLI script is the only fixed leading argument");
    }

    private static (NpmService Service, FixtureRunner Runner) CreateService(
        NpmProcessOutput outdated, NpmProcessOutput? localList = null)
    {
        var runner = new FixtureRunner(outdated, localList);
        var executables = new NpmExecutables(
            @"C:\fake\node.exe",
            @"C:\fake\npm.cmd",
            @"C:\fake\node_modules\npm\bin\npm-cli.js");
        return (new NpmService(runner, () => executables), runner);
    }

    private static async Task VerifyProcessRunnerAsync()
    {
        var runner = new NpmCommandRunner();
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Probe executable path is unavailable.");
        string[] ChildArguments(params string[] arguments) =>
            string.Equals(System.IO.Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? [typeof(Program).Assembly.Location, .. arguments]
                : arguments;

        const string singleArgument = "value with spaces & a shell-looking token";
        var echo = await runner.RunAsync(executable,
            ChildArguments("--child-probe", "echo", singleArgument), TimeSpan.FromSeconds(5));
        Check(echo.ExitCode == 7
              && echo.StandardOutput.TrimEnd('\r', '\n') == singleArgument
              && echo.StandardError.Contains("fixture stderr", StringComparison.Ordinal),
            "real runner preserves one argument and captures both streams");

        var large = await runner.RunAsync(executable,
            ChildArguments("--child-probe", "large"), TimeSpan.FromSeconds(10));
        Check(large.ExitCode == 0
              && large.StandardOutput.Length == 512 * 1024
              && large.StandardError.Length == 512 * 1024
              && large.StandardOutputTruncated && large.StandardErrorTruncated,
            "real runner drains and caps each output stream");

        var startedAt = DateTime.UtcNow;
        var timeout = await runner.RunAsync(executable,
            ChildArguments("--child-probe", "sleep"), TimeSpan.FromMilliseconds(250));
        Check(timeout.TimedOut && timeout.TerminationRequested,
            "real runner requests child termination on timeout");
        Check(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(5),
            "timeout cleanup stays bounded");
    }

    private static int RunChildProbe(string[] args)
    {
        if (args.Length == 0) return 11;
        switch (args[0])
        {
            case "echo":
                Console.WriteLine(args.Length > 1 ? args[1] : "");
                Console.Error.WriteLine("fixture stderr");
                return 7;
            case "large":
                Console.Write(new string('x', 600_000));
                Console.Error.Write(new string('y', 600_000));
                return 0;
            case "sleep":
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return 0;
            default:
                return 12;
        }
    }

    private static void Check(bool condition, string description)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException($"Assertion {_assertions} failed: {description}");
    }

    private sealed class FixtureRunner(NpmProcessOutput outdated, NpmProcessOutput? localList) : INpmCommandRunner
    {
        public List<Invocation> Calls { get; } = new();
        public List<Invocation> MutationCalls { get; } = new();
        private readonly NpmProcessOutput _localList = localList ?? Ok("""{"dependencies":{"alpha":{"version":"1.0.0"}}}""");

        public Task<NpmProcessOutput> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var invocation = new Invocation(executable, arguments.ToArray());
            Calls.Add(invocation);
            var argv = invocation.Arguments;
            NpmProcessOutput output;
            if (argv.SequenceEqual(new[] { "--version" }))
                output = Ok("v22.1.0");
            else if (argv.Length == 2 && argv[1] == "--version")
                output = Ok("10.8.0");
            else if (argv.Length == 4 && argv[1] == "config" && argv[2] == "get" && argv[3] == "prefix")
                output = Ok(@"C:\fake\global");
            else if (argv.Length >= 2 && argv[1] == "ls")
                output = _localList;
            else if (argv.Length >= 2 && argv[1] == "outdated")
                output = outdated;
            else
            {
                MutationCalls.Add(invocation);
                output = Ok("fixture mutation complete");
            }

            return Task.FromResult(output);
        }

        private static NpmProcessOutput Ok(string stdout) => new(0, stdout, "", false, false, false);
    }

    private sealed record Invocation(string Executable, string[] Arguments);
}
