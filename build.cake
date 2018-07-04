#addin "nuget:https://api.nuget.org/v3/index.json?package=Cake.Figlet&version=1.1.0"
#addin "nuget:https://api.nuget.org/v3/index.json?package=Cake.Npx&version=1.2.0"

///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////

var projectName = "Churchie";
var buildContext = new BuildContext(projectName, Context);

Action<NpxSettings> requiredSemanticVersionPackages = settings => settings
    .AddPackage("semantic-release@15.4.2")
    .AddPackage("@semantic-release/changelog@2.0.2")
    .AddPackage("@semantic-release/git@5.0.0")
    .AddPackage("@semantic-release/exec@2.2.4");

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup<BuildContext>(_ =>
{
    Information(Figlet(buildContext.ProjectName));
    Information("Target {0}", buildContext.Target);
    Information("Configuration {0}", buildContext.Configuration);

    return buildContext;
});

Teardown(context =>
{
    Information("Finished running tasks ✔");
});

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Default")
    .IsDependentOn("Build");

Task("Build")
    .IsDependentOn("dotnet --info")
    .IsDependentOn("Clean")
    .IsDependentOn("Get next release number")
    .IsDependentOn("Build solution")
    .IsDependentOn("Run tests")
    .IsDependentOn("Package")
    .IsDependentOn("Release")
    ;

Task("dotnet --info")
    .Does(() =>
{
    Information("dotnet --info");
    StartProcess("dotnet", new ProcessSettings { Arguments = "--info" });
    StartProcess("dotnet", new ProcessSettings { Arguments = "nuget locals all --list" });
});

Task("Clean")
    .Does<BuildContext>(buildContext =>
{
    Information("Cleaning {0}, bin and obj folders", buildContext.ArtifactsDir);

    CleanDirectory(buildContext.ArtifactsDir);
    CleanDirectories("./src/**/bin");
    CleanDirectories("./src/**/obj");
});

/*
Normally this task should only run based on the 'IsRunningOnAppveyorMasterBranch' condition,
however sometimes you want to run this locally to preview the next sematic version
number and changlelog.

To do this run the following locally:
> $env:NUGET_TOKEN="insert_token_here"
> $env:GITHUB_TOKEN="insert_token_here"
> .\build.ps1  -ScriptArgs '-target="Get next release number"'

NOTE: The GITHUB_TOKEN environment variable will need to be set
so that semantic-release can access the repository
*/
Task("Get next release number")
    .WithCriteria<BuildContext>(
        (_, buildContext) => buildContext.IsRunningOnAppveyorMasterBranch ||
                             buildContext.Target == "Get next release number",
        "Skipped as build not triggered by Appveyor 'master' branch commit"
    )
    .Does<BuildContext>(buildContext =>
{
    Information("Running semantic-release in dry run mode to extract next release number");

    string[] semanticReleaseOutput;
    Npx("semantic-release", "--dry-run", requiredSemanticVersionPackages, out semanticReleaseOutput);

    Information(string.Join(Environment.NewLine, semanticReleaseOutput));

    var hasReleaseVersionChanged = buildContext.SetReleaseVersionFrom(semanticReleaseOutput);

    if (hasReleaseVersionChanged)
        Information("Next release number is {0}", buildContext.ReleaseVersion);
    else
        Warning("There are no relevant changes, skipping publish to nuget");
});

Task("Build solution")
    .Does<BuildContext>(buildContext =>
{
    foreach(var solution in buildContext.Solutions)
    {
        Information("Building solution {0} v{1}", solution.GetFilenameWithoutExtension(), buildContext.ReleaseVersion);

        DotNetCoreBuild(solution.FullPath, new DotNetCoreBuildSettings()
        {
            Configuration = buildContext.Configuration,
            MSBuildSettings = new DotNetCoreMSBuildSettings()
                .SetVersion(buildContext.ReleaseVersion)
                .SetMaxCpuCount(buildContext.UseAsManyProcessesAsThereAreAvailableCPUs)
        });
    }
});

Task("Run tests")
    .Does<BuildContext>(buildContext =>
{
    foreach(var testProject in buildContext.TestProjects)
    {
        Information("Testing project {0}", testProject.GetFilenameWithoutExtension());

        DotNetCoreTest(testProject.FullPath, new DotNetCoreTestSettings
        {
            Configuration = buildContext.Configuration,
            NoBuild = true,
            NoRestore = true
        });
    }
});

Task("Package")
    .Does<BuildContext>(buildContext =>
{
    foreach(var project in buildContext.NonTestProjects)
    {
        Information("Packaging project {0} v{1}", project.GetFilenameWithoutExtension(), buildContext.ReleaseVersion);

        DotNetCorePack(project.FullPath, new DotNetCorePackSettings {
            Configuration = buildContext.Configuration,
            OutputDirectory = buildContext.ArtifactsDir,
            NoBuild = true,
            NoRestore = true,
            MSBuildSettings = new DotNetCoreMSBuildSettings()
                .SetVersion(buildContext.ReleaseVersion)
        });
    }
});

Task("Release")
    .WithCriteria<BuildContext>((_, buildContext) =>
        buildContext.IsRunningOnAppveyorMasterBranch,
        "Skipped as build not triggered by Appveyor 'master' branch commit"
    )
    .WithCriteria<BuildContext>((_, buildContext) =>
        buildContext.HasReleaseVersionChanged,
        "Skipped as release version has not changed"
    )
    .WithCriteria<BuildContext>((_, buildContext) =>
        !buildContext.IsRunningOnUnix,
        "Skipped as release was triggered by a linux build"
    )
    .Does<BuildContext>(buildContext =>
{
    Information("Releasing v{0}", buildContext.ReleaseVersion);
    Information("Updating CHANGELOG.md");
    Information("Creating github release");
    Information("Pushing to NuGet");

    Npx("semantic-release", requiredSemanticVersionPackages);
});

///////////////////////////////////////////////////////////////////////////////
// EXECUTION
///////////////////////////////////////////////////////////////////////////////

RunTarget(buildContext.Target);

///////////////////////////////////////////////////////////////////////////////
// Helpers
///////////////////////////////////////////////////////////////////////////////

public class BuildContext
{
    public const string DefaultReleaseNumber = "1.0.0";

    public string ProjectName { get; }
    public string Configuration { get; }
    public string Target { get; }
    public string ArtifactsDir { get; }
    public bool IsRunningOnAppveyorMasterBranch { get; }
    public FilePathCollection Solutions { get; }
    public FilePathCollection TestProjects { get; }
    public FilePathCollection NonTestProjects { get; }

    // 0 = use as many processes as there are available CPUs to build the project
    // see: https://develop.cakebuild.net/api/Cake.Common.Tools.MSBuild/MSBuildSettings/60E763EA
    public int UseAsManyProcessesAsThereAreAvailableCPUs { get; } = 0;

    public string ReleaseVersion { get; private set; } = DefaultReleaseNumber;
    public bool HasReleaseVersionChanged { get; private set; }
    public bool IsRunningOnUnix { get; private set; }

    public BuildContext(string projectName, ICakeContext context)
    {
        ProjectName = projectName;
        Target = context.Argument<string>("target", "Default");
        Configuration = context.Argument<string>("configuration", "Release");

        IsRunningOnUnix = context.IsRunningOnUnix();
        IsRunningOnAppveyorMasterBranch = StringComparer.OrdinalIgnoreCase.Equals(
            "master",
            context.BuildSystem().AppVeyor.Environment.Repository.Branch
        );

        ArtifactsDir =  context.Directory("./artifacts");
        Solutions = context.GetFiles("./src/*.sln");
        TestProjects = context.GetFiles("./src/**/*.Tests.csproj");
        NonTestProjects = context.GetFiles("./src/**/*.csproj") - TestProjects;
    }

    public bool SetReleaseVersionFrom(string[] semanticReleaseOutput)
    {
        var extractRegEx = new System.Text.RegularExpressions.Regex("^.+next release version is (?<SemanticVersionNumber>.*)$");

        var nextReleaseNumber = semanticReleaseOutput
            .Select(line => extractRegEx.Match(line).Groups["SemanticVersionNumber"].Value)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .SingleOrDefault();

        HasReleaseVersionChanged = nextReleaseNumber != null;
        ReleaseVersion = nextReleaseNumber ?? DefaultReleaseNumber;

        return HasReleaseVersionChanged;
    }
}
