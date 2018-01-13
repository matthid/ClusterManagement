// Learn more about F# at http://fsharp.org
// See the 'F# Tutorial' project for more help.

open System
open System.Collections
open System.Collections.Generic
open System.IO
open System.Threading
open ClusterManagement

let initTool toolName (field:string ref) =
  async {
    let! toolPath = Which.getToolPath toolName
    field := toolPath
  }

let checkDocker () =
  async {
    do! initTool "docker" DockerWrapper.dockerPath
    do! initTool "docker-machine" DockerMachineWrapper.dockerMachinePath
    do! initTool "chroot" HostInteraction.chrootPath

    if Env.isVerbose then
        printfn "Found docker at: '%s'" !DockerWrapper.dockerPath
        printfn "Found docker-machine at: '%s'" !DockerMachineWrapper.dockerMachinePath
        printfn "Found chroot at: '%s'" !DockerMachineWrapper.dockerMachinePath

    // Check if docker works
    if Env.isLinux && not (System.IO.File.Exists("/var/run/docker.sock")) then
       failwithf "Docker socket not found! Please use '-v /var/run/docker.sock:/var/run/docker.sock -v $PWD:/workDir -v /:/host' when running this from within a docker container."

    do! DockerWrapper.ensureWorking()
    do! DockerMachineWrapper.ensureWorking()
  }

open Argu

let handleArgs (argv:string array) =
    let restArgs, argv =
        match argv |> Seq.tryFindIndex (fun i -> i = "--") with
        | Some i ->
            // Leave -- in command list. This way we have proper docs and parser (list will be empty).
            argv.[i+1 .. argv.Length-1], argv.[0 .. i]
        | None ->
            [||], argv
    let parser = ArgumentParser.Create<MyArgs>(programName = "ClusterManagement.exe")
    let results =
        try
            Choice1Of2 (parser.Parse argv)
        with e ->
            Choice2Of2 e
    match results with
    | Choice1Of2 results ->
        Env.isVerbose <- results.Contains <@ MyArgs.Verbose @>
        let printVersion () = printfn "Version: 0.2.2 (%O)" (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version)
        if Env.isVerbose then
            printVersion ()
            printfn "Docker-Image: %s" DockerImages.clusterManagement
            printfn "%A %A" argv restArgs
            printfn "Interactive: %b" Env.isConsoleApp
            printfn "UserInterface: %b" Env.userInterface
            printfn "IsConsoleSizeZero: %b" IO.isConsoleSizeZero
            printfn "stdInTTy: %b" IO.stdInTTy
            let stty = Which.getToolPath "stty" |> Async.RunSynchronously
            let res =
                CreateProcess.fromRawCommand stty [|""|]
                |> CreateProcess.redirectOutput
                |> Proc.startRaw
                |> fun r -> r.GetAwaiter().GetResult()
            printfn "stty -a: %s" res.Result.Output

        if not Env.isLinux then
            eprintfn "WARN: This program was currently only tested as matthid/clustermanagement dockerized app. Running it standalone (especially on windows) might lead to bugs."
        assert (IO.stdInTTy = (not IO.isConsoleSizeZero))

        if results.Contains(<@ MyArgs.Version @>) then
            printVersion ()
            0
        else
            match results.TryGetSubCommand() with
            | Some (Cluster clusterRes) ->
                checkDocker () |> Async.RunSynchronously
                match clusterRes.TryGetSubCommand() with
                | Some (ClusterArgs.Encrypt encryptRes) ->
                    let cluster = clusterRes.GetResult <@ ClusterArgs.Cluster @>
                    let secret = encryptRes.TryGetResult <@ ClusterDecryptEncryptArgs.Secret @>
                    Cluster.encrypt cluster (Storage.withDefaultPassword secret)
                    0
                | Some (ClusterArgs.Decrypt decryptRes) ->
                    let cluster = clusterRes.GetResult <@ ClusterArgs.Cluster @>
                    let secret = decryptRes.TryGetResult <@ ClusterDecryptEncryptArgs.Secret @>
                    Cluster.decrypt cluster (Storage.withDefaultPassword secret)
                    0
                | Some (ClusterArgs.CreateNew createNewRes) ->
                    let cluster = clusterRes.GetResult <@ ClusterArgs.Cluster @>
                    let masterAsWorker = createNewRes.Contains <@ ClusterCreateNewArgs.MasterAsWorker @>
                    let masterNodes =
                        match createNewRes.TryGetResult <@ ClusterCreateNewArgs.MasterNodes @> with
                        | Some n -> n
                        | None -> 1
                    let workerNodes =
                        match createNewRes.TryGetResult <@ ClusterCreateNewArgs.WorkerNodes @> with
                        | Some n -> n
                        | None -> 0
                    let secret = Storage.withDefaultPassword (createNewRes.TryGetResult <@ ClusterCreateNewArgs.Secret @>)
                    let force = createNewRes.Contains <@ ClusterCreateNewArgs.Force @>

                    Cluster.createNewCluster force cluster secret masterNodes masterAsWorker workerNodes
                    |> Async.RunSynchronously

                    0
                | Some (ClusterArgs.Init initRes) ->
                    let cluster = clusterRes.GetResult <@ ClusterArgs.Cluster @>
                    let forceInit = initRes.Contains <@ ClusterInitArgs.Force @>
                    Cluster.init cluster forceInit
                    |> Async.RunSynchronously

                    0
                | Some (ClusterArgs.Destroy destroyArgs) ->
                    let cluster = clusterRes.GetResult <@ ClusterArgs.Cluster @>
                    let force = destroyArgs.Contains <@ ClusterDestroyArgs.Force @>
                    Cluster.destroy force cluster
                    |> Async.RunSynchronously

                    0
                | Some (ClusterArgs.Delete deleteRes) ->
                    let cluster = clusterRes.GetResult <@ ClusterArgs.Cluster @>
                    let force = deleteRes.Contains <@ ClusterDeleteArgs.Force @>
                    Cluster.delete cluster force

                    0
                | _ ->
                    printfn "Please specify a subcommand."
                    printfn "%s" (clusterRes.Parser.PrintUsage())
                    1
            | Some (List listRes) ->
                checkDocker () |> Async.RunSynchronously
                match listRes.TryGetSubCommand() with
                | Some (ListArgs.Cluster _) ->
                    let formatPrint name secretAvailable isInitialized =
                        printfn "%15s | %12s | %10s" name secretAvailable isInitialized
                    let formatPrintT name secretAvailable isInitialized =
                        formatPrint name (sprintf "%b" secretAvailable) (match isInitialized with Some b -> sprintf "%b" b| None -> "NA")
                    formatPrint "NAME" "SECRET KNWON" "INITIALIZED"
                    ClusterInfo.getClusters()
                    |> Seq.iter(fun c -> formatPrintT c.Name c.SecretAvailable c.IsInitialized)
                    0
                | _ ->
                    printfn "Please specify a subcommand."
                    printfn "%s" (listRes.Parser.PrintUsage())
                    1
            | Some (Volume res) ->
                checkDocker () |> Async.RunSynchronously
                match res.TryGetSubCommand() with
                | Some (VolumeArgs.List listRes) ->
                    let clusterName = listRes.TryGetResult <@ ListVolumeArgs.Cluster @>
                    let clusters =
                        match clusterName with
                        | Some name -> [ name ]
                        | None ->
                            ClusterInfo.getClusters()
                            |> Seq.filter (fun c -> c.SecretAvailable && c.IsInitialized.IsSome && c.IsInitialized.Value)
                            |> Seq.map (fun c -> c.Name)
                            |> Seq.toList
                    let formatPrint dataset name cluster driver =
                        printfn "%30s | %25s | %15s | %10s" dataset name cluster driver
                    let formatPrintT dataset name cluster driver =
                        formatPrint dataset name cluster driver
                    formatPrint "NAME" "SIMPLENAME" "CLUSTER" "DRIVER"
                    for c in clusters do
                        Storage.openClusterWithStoredSecret c
                        let volumes =
                            Volume.list c
                            |> Async.RunSynchronously
                        for vol in volumes do
                            let cl =
                                match vol.ClusterInfo with
                                | Some ci -> ci.Cluster
                                | None -> "<NONE>"
                            formatPrintT vol.Info.Name vol.SimpleName cl vol.Info.Driver
                        Storage.closeClusterWithStoredSecret c
                    0
                | Some (VolumeArgs.Create createArgs) ->
                    let clusterName = createArgs.GetResult <@ VolumeCreateArgs.Cluster @>
                    let name = createArgs.GetResult <@ VolumeCreateArgs.Name @>
                    let isGlobal = createArgs.Contains <@ VolumeCreateArgs.Global @>
                    let size =
                        match createArgs.TryGetResult <@ VolumeCreateArgs.Size @> with
                        | Some s -> s
                        | None -> 1024L * 1024L * 1024L

                    Storage.openClusterWithStoredSecret clusterName
                    Volume.create isGlobal clusterName name size
                    |> Async.RunSynchronously
                    |> ignore
                    Storage.closeClusterWithStoredSecret clusterName

                    0
                | Some (VolumeArgs.Upload uploadArgs) ->
                    let clusterName = uploadArgs.GetResult <@ VolumeCopyContentsArgs.Cluster @>
                    let name = uploadArgs.GetResult <@ VolumeCopyContentsArgs.Volume @>
                    let localFolder = uploadArgs.GetResult <@ VolumeCopyContentsArgs.LocalFolder @>
                    let fileName =
                        match uploadArgs.TryGetResult <@ VolumeCopyContentsArgs.FileName @> with
                        | Some f -> f
                        | None -> "."

                    Storage.openClusterWithStoredSecret clusterName
                    Volume.copyContents fileName CopyDirection.Upload clusterName name localFolder
                    |> Async.RunSynchronously

                    Storage.closeClusterWithStoredSecret clusterName

                    0
                | Some (VolumeArgs.Download downloadArgs) ->
                    let clusterName = downloadArgs.GetResult <@ VolumeCopyContentsArgs.Cluster @>
                    let name = downloadArgs.GetResult <@ VolumeCopyContentsArgs.Volume @>
                    let localFolder = downloadArgs.GetResult <@ VolumeCopyContentsArgs.LocalFolder @>
                    let fileName =
                        match downloadArgs.TryGetResult <@ VolumeCopyContentsArgs.FileName @> with
                        | Some f -> f
                        | None -> "."

                    Storage.openClusterWithStoredSecret clusterName
                    Volume.copyContents fileName CopyDirection.Download clusterName name localFolder
                    |> Async.RunSynchronously

                    Storage.closeClusterWithStoredSecret clusterName
                    0
                | Some (VolumeArgs.Clone _) ->
                    printfn "Not implemented."
                    1
                | Some (VolumeArgs.Delete deleteArgs) ->
                    let clusterName = deleteArgs.GetResult <@ VolumeDeleteArgs.Cluster @>
                    let name = deleteArgs.GetResult <@ VolumeDeleteArgs.Name @>

                    Storage.openClusterWithStoredSecret clusterName
                    Volume.remove clusterName name
                    |> Async.RunSynchronously
                    |> ignore
                    Storage.closeClusterWithStoredSecret clusterName

                    0
                | None ->
                    printfn "Please specify a subcommand."
                    printfn "%s" (res.Parser.PrintUsage())
                    1
            | Some (DockerMachine res) ->
                checkDocker () |> Async.RunSynchronously
                match res.TryGetResult <@ DockerMachineArgs.Cluster @> with
                | Some (name) ->
                    Storage.openClusterWithStoredSecret name
                    let res =
                        DockerMachine.createProcess name (restArgs |> Arguments.OfArgs)
                        |> Proc.startRaw
                        |> Async.AwaitTask
                        |> Async.RunSynchronously

                    Storage.closeClusterWithStoredSecret name
                    res.ExitCode
                | None ->
                    printfn "Please specify a cluster to run this command for."
                    printfn "%s" (res.Parser.PrintUsage())
                    1

            | Some (Config configRes) ->
                checkDocker () |> Async.RunSynchronously
                match configRes.TryGetSubCommand() with
                | Some (ConfigArgs.Upload uploadArgs) ->
                    //uploadArgs
                    let clusterName = uploadArgs.GetResult <@ ConfigUploadArgs.Cluster @>
                    let filePath =
                        uploadArgs.GetResult <@ ConfigUploadArgs.FilePath @>
                        |> DockerWrapper.mapGivenPath

                    let name = uploadArgs.GetResult <@ ConfigUploadArgs.Name @>

                    let data = File.ReadAllBytes(filePath)
                    Storage.openClusterWithStoredSecret clusterName
                    ConfigStorage.writeFile clusterName name data
                    Storage.closeClusterWithStoredSecret clusterName
                    0
                | Some (ConfigArgs.Download downloadArgs) ->
                    let clusterName = downloadArgs.GetResult <@ ConfigDownloadArgs.Cluster @>
                    let filePath =
                        downloadArgs.GetResult <@ ConfigDownloadArgs.FilePath @>
                        |> DockerWrapper.mapGivenPath
                    let name = downloadArgs.GetResult <@ ConfigDownloadArgs.Name @>

                    Storage.openClusterWithStoredSecret clusterName
                    let res = ConfigStorage.tryReadFile clusterName name
                    Storage.closeClusterWithStoredSecret clusterName
                    match res with
                    | Some va ->
                        File.WriteAllBytes(filePath, va)
                        0
                    | None ->
                        103
                | Some (ConfigArgs.Get getArgs) ->
                    let name = getArgs.GetResult <@ ConfigGetArgs.Key @>
                    let clusterName = getArgs.GetResult <@ ConfigGetArgs.Cluster @>

                    Storage.openClusterWithStoredSecret clusterName
                    let c = ClusterConfig.readClusterConfig clusterName
                    let res = ClusterConfig.getConfig name c
                    Storage.closeClusterWithStoredSecret clusterName
                    match res with
                    | Some va ->
                        printfn "%s" va
                        0
                    | None ->
                        103
                | Some (ConfigArgs.Set setArgs) ->
                    let name = setArgs.GetResult <@ ConfigSetArgs.Key @>
                    let value = setArgs.GetResult <@ ConfigSetArgs.Value @>
                    let clusterName = setArgs.GetResult <@ ConfigSetArgs.Cluster @>

                    Storage.openClusterWithStoredSecret clusterName
                    Config.set clusterName name value
                    Storage.closeClusterWithStoredSecret clusterName
                    0
                | Some (ConfigArgs.Copy copyArgs) ->
                    let source = copyArgs.GetResult <@ ConfigCopyArgs.Source @>
                    let dest = copyArgs.GetResult <@ ConfigCopyArgs.Dest @>

                    Storage.openClusterWithStoredSecret source
                    Storage.openClusterWithStoredSecret dest

                    Config.cloneConfig source dest

                    Storage.closeClusterWithStoredSecret source
                    Storage.closeClusterWithStoredSecret dest

                    0
                | Some (ConfigArgs.List listArgs) ->
                    let clusterName = listArgs.TryGetResult <@ ListConfigArgs.Cluster @>
                    let includeValues = listArgs.Contains <@ ListConfigArgs.IncludeValues @>
                    let includeFiles = listArgs.Contains <@ ListConfigArgs.IncludeFiles @>
                    let clusters =
                        match clusterName with
                        | Some name -> [ name ]
                        | None ->
                            ClusterInfo.getClusters()
                            |> Seq.filter (fun c -> c.SecretAvailable)
                            |> Seq.map (fun c -> c.Name)
                            |> Seq.toList
                    let formatPrint key value cluster =
                        if includeValues then
                            printfn "%25s | %25s | %20s" key value cluster
                        else
                            printfn "%25s | %20s" key cluster
                    let formatPrintT key value cluster =
                        formatPrint key value cluster
                    formatPrint "KEY" "VALUE" "CLUSTER"
                    for c in clusters do
                        Storage.openClusterWithStoredSecret c
                        let cc = ClusterConfig.readClusterConfig c
                        let toks = ClusterConfig.getTokens cc
                        for tok in toks do
                            formatPrintT tok.Name tok.Value c


                        if includeFiles then
                            for f in ConfigStorage.getFiles c do
                                formatPrintT f "<file>" c


                        Storage.closeClusterWithStoredSecret c
                    0
                | Some (ConfigArgs.UpdateCluster updateArgs) ->
                    let clusterName = updateArgs.GetResult <@ ConfigUpdateClusterArgs.Cluster @>

                    // We simply re-deploy clustermanagement
                    Deploy.deployIntegrated clusterName "DeployClusterManagement.fsx"
                    0
                | _ ->
                    printfn "Please specify a subcommand."
                    printfn "%s" (configRes.Parser.PrintUsage())
                    1
            | Some (Provision res) ->
                checkDocker () |> Async.RunSynchronously
                let nodeName = res.GetResult <@ ProvisionArgs.NodeName @>
                let clusterName = res.GetResult <@ ProvisionArgs.Cluster @>
                let nodeType = res.GetResult <@ ProvisionArgs.NodeType @>
                Cluster.provision clusterName nodeName nodeType
                |> Async.RunSynchronously
                0
            | Some (Run res) ->
                checkDocker () |> Async.RunSynchronously
                let script = res.GetResult <@ RunArgs.Script @>
                let clusterName = res.GetResult <@ RunArgs.Cluster @>
                Deploy.deploy clusterName script restArgs
                0
            | Some (Service res) ->
                checkDocker () |> Async.RunSynchronously
                raise <| NotImplementedException "not implemented"
                //let script = res.GetResult <@ RunArgs.Script @>
                //let clusterName = res.GetResult <@ RunArgs.Cluster @>
                //Deploy.deploy clusterName script restArgs
                0
            | Some (Export res) ->
                checkDocker () |> Async.RunSynchronously
                // TODO: make sure to export flocker-container as well
                raise <| NotImplementedException "not implemented"
                0
            | Some (Import res) ->
                checkDocker () |> Async.RunSynchronously
                // TODO: make sure to import flocker-container as well
                raise <| NotImplementedException "not implemented"
                0
            | Some (Internal internalRes) ->
                match internalRes.TryGetSubCommand() with
                | Some (ServeConfig res) ->
                    ServeConfig.startServer ()
                    0
                | Some (OpenCluster res) ->
                    checkDocker () |> Async.RunSynchronously
                    let clusterName = res.GetResult <@ OpenClusterArgs.Cluster @>
                    Storage.openClusterWithStoredSecret clusterName
                    0
                | Some (CloseCluster res) ->
                    checkDocker () |> Async.RunSynchronously
                    let clusterName = res.GetResult <@ CloseClusterArgs.Cluster @>
                    Storage.closeClusterWithStoredSecret clusterName
                    0
                | Some (DeployConfig res) ->
                    // Upload stuff from /config to http://clustermanagement/v1/config-files/
                    let basePath = "/config"
                    let files =
                        System.IO.Directory.GetFiles(basePath, "*", System.IO.SearchOption.AllDirectories)
                        |> Seq.map (fun s -> s.Substring (basePath.Length + 1))
                        |> Seq.toList
                    ( let client = new System.Net.Http.HttpClient()
                      try
                        for f in files do
                            let uri = "http://clustermanagement/v1/config-files/" + f
                            let fileName = Path.GetFileName f
                            let fileNameWithoutExtension = Path.GetFileNameWithoutExtension f
                            printfn "Uploading '%s' to '%s'" f uri
                            let requestContent = new System.Net.Http.MultipartFormDataContent()
                            requestContent.Add(new System.Net.Http.StringContent(fileNameWithoutExtension), fileNameWithoutExtension)
                            use fstream = File.OpenRead(Path.Combine (basePath, f))
                            let fileContent = new System.Net.Http.StreamContent(fstream)
                            fileContent.Headers.ContentType <-
                                System.Net.Http.Headers.MediaTypeHeaderValue.Parse "application/octet-stream"
                            fileContent.Headers.ContentDisposition <-
                                new System.Net.Http.Headers.ContentDispositionHeaderValue(
                                    "form-data",
                                    Name = fileNameWithoutExtension,
                                    FileName = fileName)
                            requestContent.Add(fileContent)
                            let result =
                                client.PutAsync(uri, requestContent)
                                |> Async.AwaitTask
                                |> Async.RunSynchronously
                            let res = result.Content.ReadAsStringAsync().Result
                            if not result.IsSuccessStatusCode then
                                eprintfn "%s" res
                                result.EnsureSuccessStatusCode() |> ignore
                      finally
                        try
                            client.Dispose()
                        with e ->
                            eprintfn "Error while disposing 'client': %O" e)
                    0
                | _ ->
                    printfn "Please specify a subcommand."
                    printfn "%s" (internalRes.Parser.PrintUsage())
                    1
            | _ ->
                printfn "Please specify a subcommand."
                printfn "%s" (parser.PrintUsage())
                1
    | Choice2Of2 e ->
        printfn "%s" e.Message
        1

[<EntryPoint>]
let main argv =
    //Console.
    try
        handleArgs argv
    with e ->
        if Env.isVerbose then
            eprintfn "%O" e
        else
            eprintfn "%s" e.Message
        2