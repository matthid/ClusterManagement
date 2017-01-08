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
    let! toolPath = Which.getTooPath toolName
    field := toolPath
  }
    
let checkDocker () =
  async {
    do! initTool "docker" DockerWrapper.dockerPath
    do! initTool "docker-machine" DockerMachineWrapper.dockerMachinePath

    if Env.isVerbose then
        printfn "Found docker at: '%s'" !DockerWrapper.dockerPath
        printfn "Found docker-machine at: '%s'" !DockerMachineWrapper.dockerMachinePath
    
    // Check if docker works
    if Env.isLinux && not (System.IO.File.Exists("/var/run/docker.sock")) then
       failwithf "Docker socket not found! Please use '-v /var/run/docker.sock:/var/run/docker.sock -v /mydir/on/host:/clustercfg' when running this from within a docker container."

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
        if Env.isVerbose then
            printfn "%A %A" argv restArgs
            printfn "Interactive: %b" Env.isConsoleApp
            printfn "UserInterface: %b" Env.userInterface
            printfn "IsConsoleSizeZero: %b" Env.isConsoleSizeZero
            printfn "stdInTTy: %b" Env.stdInTTy
            let stty = Which.getTooPath "stty" |> Async.RunSynchronously
            let res = Proc.startProcess stty "-a" |> Async.RunSynchronously
            printfn "stty -a: %s" res.Output.StdOut 
        
        if not Env.isLinux then
            eprintfn "WARN: This program was currently only tested as matthid/clustermanagement dockerized app. Running it standalone (especially on windows) might lead to bugs."        
        assert (Env.stdInTTy = (not Env.isConsoleSizeZero))

        if results.Contains(<@ MyArgs.Version @>) then
            printfn "Version: 0.1.0"
            0
        else
            checkDocker () |> Async.RunSynchronously 
            match results.TryGetSubCommand() with
            | Some (Cluster clusterRes) ->
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
                | Some (ClusterArgs.Destroy _) ->
                    let cluster = clusterRes.GetResult <@ ClusterArgs.Cluster @>
                    Cluster.destroy cluster
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
                    let formatPrint dataset name size cluster =
                        printfn "%20s | %20s | %7s | %10s | %20s" dataset name size cluster
                    let formatPrintT dataset name size cluster =
                        formatPrint dataset name size cluster
                    formatPrint "DATASET" "NAME" "SIZE" "STATUS" "CLUSTER"
                    for c in clusters do
                        Storage.openClusterWithStoredSecret c
                        let volumes =
                            Volume.list c
                            |> Async.RunSynchronously
                        for vol in volumes do
                            formatPrintT vol.Dataset vol.Metadata vol.Size vol.Status c
                        Storage.closeClusterWithStoredSecret c
                    0
                | Some (VolumeArgs.Create createArgs) ->
                    let clusterName = createArgs.GetResult <@ VolumeCreateArgs.Cluster @>
                    let name = createArgs.GetResult <@ VolumeCreateArgs.Name @>
                    let size =
                        match createArgs.TryGetResult <@ VolumeCreateArgs.Size @> with
                        | Some s -> s
                        | None -> 1024L * 1024L * 1024L
                    
                    Volume.create clusterName name size
                    |> Async.RunSynchronously

                    0
                | Some (VolumeArgs.Clone _) ->
                    printfn "Not implemented."
                    1
                | Some (VolumeArgs.Delete deleteArgs) ->
                    let clusterName = deleteArgs.GetResult <@ VolumeDeleteArgs.Cluster @>
                    let name = deleteArgs.GetResult <@ VolumeDeleteArgs.Name @>
                    
                    Volume.destroy clusterName name
                    |> Async.RunSynchronously

                    0
                | None ->
                    printfn "Please specify a subcommand."
                    printfn "%s" (res.Parser.PrintUsage())
                    1
            | Some (DockerMachine res) ->
                match res.TryGetResult <@ DockerMachineArgs.Cluster @> with
                | Some (name) ->
                    Storage.openClusterWithStoredSecret name
                    let res =
                        DockerMachine.runInteractive name (Proc.argvToCommandLine restArgs)
                        |> Async.RunSynchronously
                    Storage.closeClusterWithStoredSecret name
                    0
                | None ->
                    printfn "Please specify a cluster to run this command for."
                    printfn "%s" (res.Parser.PrintUsage())
                    1
                    
            | Some (Config configRes) ->
                match configRes.TryGetSubCommand() with
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
                        Storage.closeClusterWithStoredSecret c
                    0
                | _ ->
                    printfn "Please specify a subcommand."
                    printfn "%s" (parser.PrintUsage())
                    1
            | Some (Provision res) ->
                let nodeName = res.GetResult <@ ProvisionArgs.NodeName @>
                let clusterName = res.GetResult <@ ProvisionArgs.Cluster @>
                let nodeType = res.GetResult <@ ProvisionArgs.NodeType @>
                Cluster.provision clusterName nodeName nodeType
                |> Async.RunSynchronously
                0
            | Some (Deploy res) ->
                let script = res.GetResult <@ DeployArgs.Script @>
                let clusterName = res.GetResult <@ DeployArgs.Cluster @>
                Deploy.deploy clusterName script restArgs
                0
            | Some (Export res) ->
                // TODO: make sure to export flocker-container as well
                raise <| NotImplementedException "not implemented"
                0
            | Some (Import res) ->
                // TODO: make sure to import flocker-container as well
                raise <| NotImplementedException "not implemented"
                0
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