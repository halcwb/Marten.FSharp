#r @"packages/build/FAKE/tools/FakeLib.dll"
open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open Fake.UserInputHelper
open System

let release = LoadReleaseNotes "RELEASE_NOTES.md"
let srcGlob = "src/**/*.fsproj"
let testsGlob = "tests/**/*.fsproj"

Target "Clean" (fun _ ->
    ["bin"; "temp" ;"dist"]
    |> CleanDirs

    !! srcGlob
    ++ testsGlob
    |> Seq.collect(fun p ->
        ["bin";"obj"]
        |> Seq.map(fun sp ->
             IO.Path.GetDirectoryName p @@ sp)
        )
    |> CleanDirs

    )

Target "DotnetRestore" (fun _ ->
    !! srcGlob
    ++ testsGlob
    |> Seq.iter (fun proj ->
        DotNetCli.Restore (fun c ->
            { c with
                Project = proj
                //This makes sure that Proj2 references the correct version of Proj1
                AdditionalArgs = [sprintf "/p:PackageVersion=%s" release.NugetVersion]
            })
))

Target "DotnetBuild" (fun _ ->
    !! srcGlob
    |> Seq.iter (fun proj ->
        DotNetCli.Build (fun c ->
            { c with
                Project = proj
                //This makes sure that Proj2 references the correct version of Proj1
                AdditionalArgs = [sprintf "/p:PackageVersion=%s" release.NugetVersion]
            })
))

let whoami () =
    let result =
        ExecProcessAndReturnMessages (fun s -> s.FileName <- "whoami") (TimeSpan.FromSeconds(1.))
    result.Messages |> Seq.head



let invoke f = f ()
let invokeAsync f = async { f () }

type TargetFramework =
| Full of string
| Core of string

let getTargetFramework tf =
    match tf with
    | "net45" | "net451" | "net452"
    | "net46" | "net461" | "net462" ->
        Full tf
    | "netcoreapp1.0" | "netcoreapp1.1" ->
        Core tf
    | _ -> failwithf "Unknown TargetFramework %s" tf

let getTargetFrameworksFromProjectFile (projFile : string)=
    let doc = Xml.XmlDocument()
    doc.Load(projFile)
    doc.GetElementsByTagName("TargetFrameworks").[0].InnerText.Split(';')
    |> Seq.map getTargetFramework
    |> Seq.toList

let selectRunnerForFramework tf =
    let runMono = sprintf "mono -f %s -c Release"
    let runCore = sprintf "run -f %s -c Release"
    match tf with
    | Full t when isMono-> runMono t
    | Full t -> runCore t
    | Core t -> runCore t


let setEnvToIfNotExist key value =

    match environVarOrNone key with
    | None ->
        setEnvironVar key value
        environVar key |> logfn "Env %s now set to %s \n" key
    | Some v ->
        v |> logfn "Env %s already set to %s \n" key



let setPostgresEnvVars () =
    //if environment variables aren't set, assume defaults
    setEnvToIfNotExist "POSTGRES_HOST" "localhost"
    //Postgres.app on osx default user is the person, not postgres
    setEnvToIfNotExist "POSTGRES_USER" (if isMacOS then (whoami ()) else "postgres")
    setEnvToIfNotExist "POSTGRES_PASS" "postgres"
    setEnvToIfNotExist "POSTGRES_DB" "postgres"

let runTests modifyArgs =
    setPostgresEnvVars ()

    !! testsGlob
    |> Seq.map(fun proj -> proj, getTargetFrameworksFromProjectFile proj)
    |> Seq.collect(fun (proj, targetFrameworks) ->
        targetFrameworks
        |> Seq.map selectRunnerForFramework
        |> Seq.map(fun args -> fun () ->
            DotNetCli.RunCommand (fun c ->
            { c with
                WorkingDir = IO.Path.GetDirectoryName proj
            }) (modifyArgs args))
    )



Target "DotnetTest" (fun _ ->
    runTests id
    |> Seq.toArray
    |> Array.Parallel.iter (invoke)
)
let execProcAndReturnMessages filename args =
    let args' = args |> String.concat " "
    ProcessHelper.ExecProcessAndReturnMessages
                (fun psi ->
                    psi.FileName <- filename
                    psi.Arguments <-args'
                ) (TimeSpan.FromMinutes(1.))

let pkill args =
    execProcAndReturnMessages "pkill" args

let killParentsAndChildren processId=
    pkill [sprintf "-P %d" processId]


Target "WatchTests" (fun _ ->
    runTests (sprintf "watch %s")
    |> Seq.iter (invokeAsync >> Async.Catch >> Async.Ignore >> Async.Start)

    printfn "Press enter to stop..."
    Console.ReadLine() |> ignore

    if isWindows |> not then
        startedProcesses
        |> Seq.iter(fst >> killParentsAndChildren >> ignore )
    else
        //Hope windows handles this right?
        ()
)


Target "DotnetPack" (fun _ ->
    !! srcGlob
    |> Seq.iter (fun proj ->
        DotNetCli.Pack (fun c ->
            { c with
                Project = proj
                Configuration = "Release"
                OutputPath = IO.Directory.GetCurrentDirectory() @@ "dist"
                AdditionalArgs = [sprintf "/p:PackageVersion=%s" release.NugetVersion]
            })
    )
)

Target "Publish" (fun _ ->
    Paket.Push(fun c ->
            { c with
                PublishUrl = "https://www.nuget.org"
                WorkingDir = "dist"
            }
        )
)

Target "Release" (fun _ ->

    if Git.Information.getBranchName "" <> "master" then failwith "Not on master"

    let releaseNotesGitCommitFormat = ("",release.Notes |> Seq.map(sprintf "* %s\n")) |> String.Join

    StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s \n%s" release.NugetVersion releaseNotesGitCommitFormat)
    Branches.push ""

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" "origin" release.NugetVersion
)

"Clean"
  ==> "DotnetRestore"
  ==> "DotnetBuild"
  ==> "DotnetTest"
  ==> "DotnetPack"
  ==> "Publish"
  ==> "Release"

RunTargetOrDefault "DotnetPack"
