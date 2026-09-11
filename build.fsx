#!dotnet fsi
#r "nuget: Fake.Core.Target, 6.0"
#r "nuget: Fake.DotNet.Cli, 6.0"
#r "nuget: Fake.IO.FileSystem, 6.0"
#r "nuget: Fake.Core.ReleaseNotes, 6.0"

module Utils =
    open Fake.Core
    open Fake.DotNet
    open Fake.IO
    open Fake.IO.FileSystemOperators
    open Fake.IO.Globbing.Operators

    let dotnet cmd workingDir =
        let result = DotNet.exec (DotNet.Options.withWorkingDirectory workingDir) cmd ""
        if result.ExitCode <> 0 then failwithf "'dotnet %s' failed in %s" cmd workingDir

    let cleanBinAndObj projectPath =
        try
            Shell.cleanDirs [
                projectPath </> "bin"
                projectPath </> "obj"
            ]
        with _ -> ()

    let initFakeRuntime () =
        System.Environment.GetCommandLineArgs()
        |> Array.skip 2 // skip fsi.exe; build.fsx
        |> Array.toList
        |> Context.FakeExecutionContext.Create false __SOURCE_FILE__
        |> Context.RuntimeContext.Fake
        |> Context.setExecutionContext

        Target.initEnvironment ()

    let findPackPath dir =
        let packPathPattern =
            dir </> "*.nupkg"

        !! packPathPattern
        |> Seq.truncate 2
        |> List.ofSeq
        |> function
            | [nupkgPath] -> nupkgPath
            | [] ->
                failwithf "'%s' not found" packPathPattern
            | nupkgPaths ->
                failwithf "More than one *.nupkg found: '%A'" nupkgPaths

// --------------------------------------------------------------------------------------
// Build variables
// --------------------------------------------------------------------------------------
let commonBuildArgs = "-c Release"

module XmlText =
    let escape rawText =
        let doc = new System.Xml.XmlDocument()
        let node = doc.CreateElement("root")
        node.InnerText <- rawText
        node.InnerXml

Utils.initFakeRuntime ()

module CoreProject =
    open Fake.Core
    open Fake.Core.TargetOperators
    open Fake.IO
    open Fake.IO.FileSystemOperators

    open Utils

    let prefix = "Core"

    let projectPath = "src/Core.fsproj"
    let projectDirectory = Path.getDirectory projectPath
    let deployDirectory = Path.getFullName "./deploy"
    let releasePath = projectDirectory </> "RELEASE_NOTES.md"

    let cleanTarget = prefix + "Clean"
    Target.create cleanTarget (fun _ ->
        cleanBinAndObj projectDirectory
    )

    let buildTarget = prefix + "Build"
    Target.create buildTarget (fun _ ->
        projectDirectory
        |> dotnet (sprintf "build %s" commonBuildArgs)
    )

    let deployCleanTarget = prefix + "DeployClean"
    Target.create deployCleanTarget (fun _ ->
        Shell.cleanDir deployDirectory
    )

    let deployTarget = prefix + "Deploy"
    Target.create deployTarget (fun _ ->
        projectDirectory
        |> dotnet (sprintf "build %s -o \"%s\"" commonBuildArgs deployDirectory)
    )

    let metaTarget = prefix + "Meta"
    Target.create metaTarget (fun _ ->
        let release = ReleaseNotes.load releasePath

        [
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
            "<ItemGroup>"
            "    <PackageReference Include=\"Microsoft.SourceLink.GitHub\" Version=\"1.0.0\" PrivateAssets=\"All\"/>"
            "</ItemGroup>"
            "<PropertyGroup>"
            "    <EmbedUntrackedSources>true</EmbedUntrackedSources>"
            "    <PackageProjectUrl>https://github.com/gretmn102/VideoReviewCreator.Core/tree/main/src</PackageProjectUrl>"
            "    <PackageLicenseExpression>MIT</PackageLicenseExpression>"
            "    <RepositoryUrl>https://github.com/gretmn102/VideoReviewCreator.Core.git</RepositoryUrl>"
            sprintf "    <PackageReleaseNotes>%s</PackageReleaseNotes>"
                (String.concat "\n" release.Notes |> XmlText.escape)
            "    <PackageTags>fsharp;ffmpeg</PackageTags>"
            "    <Authors>agentlapki</Authors>"
            sprintf "    <Version>%s</Version>" (string release.SemVer)
            "</PropertyGroup>"
            "</Project>"
        ]
        |> File.write false (
            projectDirectory </> "Directory.Build.props"
        )
    )

    let packTarget = prefix + "Pack"
    Target.create packTarget (fun _ ->
        projectDirectory
        |> dotnet (sprintf "pack %s -o \"%s\"" commonBuildArgs deployDirectory)
    )

    let publishToGitlab = prefix + "PublishToGitlab"
    Target.create publishToGitlab (fun _ ->
        let path = findPackPath deployDirectory
        let source = "https://gitlab.com/api/v4/projects/28574921/packages/nuget/index.json"
        let apiKey = Environment.environVarOrFail "GITLAB_DEPLOY_TOKEN"
        "."
        |> dotnet (
            String.concat " " [
                "nuget"
                "push"
                $"--source {source}"
                $"--api-key {apiKey}"
                "--skip-duplicate"
                $"{path}"
            ]
        )
    )

    deployCleanTarget
        ==> metaTarget
        ==> deployTarget

    deployCleanTarget
        ==> metaTarget
        ==> packTarget

    packTarget ==> publishToGitlab

open Fake.Core
open Fake.Core.TargetOperators

try
    Target.runOrDefault CoreProject.deployTarget
with _ ->
    System.Environment.Exit(1)
