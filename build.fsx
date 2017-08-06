// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------

#r @"packages/build/FAKE/tools/FakeLib.dll"

open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open System
open System.IO

// Information about the project are used
//  - for version and project name in generated AssemblyInfo file
//  - by the generated NuGet package
//  - to run tests and to publish documentation on GitHub gh-pages
//  - for documentation, you also need to edit info in "docs/tools/generate.fsx"

// The name of the project
// (used by attributes in AssemblyInfo, name of a NuGet package and directory in 'src')
let project = "CLusterManagement"

// Short summary of the project
// (used as description in AssemblyInfo and as a short summary for NuGet package)
let summary = "Railway-oriented programming for .NET"

// Longer description of the project
// (used as a description for NuGet package; line breaks are automatically cleaned up)
let description = "Railway-oriented programming for .NET"

// List of author names (for NuGet package)
let authors = [ "Steffen Forkmann"; "Max Malook"; "Tomasz Heimowski" ]

// Tags for your project (for NuGet package)
let tags = "rop, fsharp, F#"

// File system information 
let solutionFile  = "ClusterManagement.sln"

// Pattern specifying assemblies to be tested using NUnit
let testAssemblies = "tests/**/bin/Release/*Tests*.dll"

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "fsprojects" 
let gitHome = "https://github.com/" + gitOwner

// The name of the project on GitHub
let gitName = "Chessie"

// The url for the raw files hosted
let gitRaw = environVarOrDefault "gitRaw" "https://raw.github.com/fsprojects"

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps
// --------------------------------------------------------------------------------------

// Read additional information from the release notes document
//let release = LoadReleaseNotes "RELEASE_NOTES.md"

// --------------------------------------------------------------------------------------
// Clean build results

Target "Clean" (fun _ ->
    CleanDirs ["bin"; "temp"]
    !! "**/project.lock.json" |> DeleteFiles
)


// --------------------------------------------------------------------------------------
// Build library & test project

Target "Build" (fun _ ->
    !! solutionFile
    |> MSBuildRelease "" "Rebuild"
    |> ignore
    
    CopyFile "ClusterManagement/bin/Release/FSharp.Data.DesignTime.dll" "packages/FSharp.Data/lib/net40/FSharp.Data.DesignTime.dll"
)


Target "BuildDocker" (fun _ ->
    let res = 
        ProcessHelper.ExecProcess (fun c ->
            c.FileName <- "docker"
            c.Arguments <- "build -t clustermanagement .")
            (TimeSpan.FromMinutes 40.0)
    if res <> 0 then failwithf "docker failed with exit code '%d'" res
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

Target "RunTests" (fun _ ->
    !! testAssemblies
    |> NUnit (fun p ->
        { p with
            DisableShadowCopy = true
            TimeOut = TimeSpan.FromMinutes 20.
            OutputFile = "TestResults.xml" })
)

// --------------------------------------------------------------------------------------
// Release Scripts

Target "All" DoNothing

"Clean"
  ==> "Build"
  ==> "All"

RunTargetOrDefault "All"
