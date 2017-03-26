namespace ClusterManagement


module Cluster =
    let encrypt cluster secret =
        let c = GlobalConfig.readConfig()
        let clusterAvail = Storage.isClusterAvailable cluster
        if clusterAvail then
            match GlobalConfig.readSecret cluster c with
            | Some oldSecret ->
                    try
                        Storage.openCluster cluster oldSecret
                    with e ->
                        if Env.isVerbose then
                            eprintfn "WARN: Could not open cluster with old secret: %O" e
            | None -> ()
            try
                Storage.openCluster cluster secret
            with _ ->
                eprintfn "Could not open cluster with either old or new password" 
                reraise()
        
            // encrypt with new password
            Storage.closeCluster cluster secret
        // Store new secret
        c
        |> GlobalConfig.setSecret cluster secret
        |> GlobalConfig.writeConfig

    let decrypt cluster secret =
        let c = GlobalConfig.readConfig()
        let clusterAvail = Storage.isClusterAvailable cluster
        let useNewSecret =
            if clusterAvail then
                let wasOpened =
                    match GlobalConfig.readSecret cluster c with
                    | Some oldSecret ->
                        try
                            Storage.openCluster cluster oldSecret
                            eprintfn "WARN: Could open cluster with old secret. Ignoring new one. To change secret use 'encrypt'."
                            true
                        with _ ->
                            false
                    | None -> false
                if not wasOpened then
                    try
                        Storage.openCluster cluster secret
                    with _ ->
                        eprintfn "Could not open cluster with either old or new password" 
                        reraise()
                not wasOpened
            else true
        if useNewSecret then
            // Store new secret
            c
            |> GlobalConfig.setSecret cluster secret
            |> GlobalConfig.writeConfig

    open System.IO
    let createNewCluster force clusterName secret masterNodes masterAsWorker workerNodes =
      async {
        if Storage.isClusterAvailable clusterName then
            if not force then
                failwithf "cluster '%s' is already created, use force! Note that force MIGH make previous flocker volumes INACCESSIBLE via clustermanagement!" clusterName
            else
                eprintfn "Re-creating initial certificates. This action MIGHT make previous flocker volumes INACCESSIBLE via clustermanagement!"
            Storage.openClusterWithStoredSecret clusterName

        let config = StoragePath.getGlobalConfigDir clusterName
        // create cluster.key and cluster.crt
        let clusterKeyName = "cluster.key"
        let clusterCrtName = "cluster.crt"
        let clusterKey = Path.Combine (config, clusterKeyName)
        let clusterCrt = Path.Combine (config, clusterCrtName)
        if not (File.Exists clusterKey) || not (File.Exists clusterCrt) then
            if File.Exists clusterKey then File.Delete clusterKey
            if File.Exists clusterCrt then File.Delete clusterCrt
            let! res = 
                DockerWrapper.flockerca config (Arguments.OfArgs [| "initialize"; clusterName|]).ToWindowsCommandLine
                |> Proc.startAndAwait
            if not (File.Exists clusterKey) || not (File.Exists clusterCrt) then
                failwithf "flockerca failed to create '%s' or '%s'" clusterKeyName clusterCrtName
        
        let copyClusterCertAndKey dir =
            File.Copy (clusterKey, Path.Combine(dir, clusterKeyName), true)
            File.Copy (clusterCrt, Path.Combine(dir, clusterCrtName), true)
        let removeClusterKey dir =
            File.Delete (Path.Combine(dir, clusterKeyName))
        let initFlocker dir f =
          async {
            let flockerDir = StoragePath.ensureAndReturnDir (Path.Combine (dir, "etc", "flocker"))
            try
                copyClusterCertAndKey flockerDir
                return! f flockerDir
            finally
                removeClusterKey flockerDir
          }
        let deleteIfExists f =
            if File.Exists f then File.Delete f
            
        let handle source dest =
            if File.Exists dest then
                File.Delete source
            else File.Move(source, dest)
        let createMasterConfig dir =
            let nodeName = Storage.getNodeNameByDir dir
            initFlocker dir (fun flockerDir ->
              async {
                let! res = 
                    DockerWrapper.flockerca flockerDir (sprintf "create-control-certificate %s" nodeName)
                    |> Proc.startAndAwait
                handle (Path.Combine(flockerDir, sprintf "control-%s.crt" nodeName)) (Path.Combine(flockerDir, "control-service.crt"))
                handle (Path.Combine(flockerDir, sprintf "control-%s.key" nodeName)) (Path.Combine(flockerDir, "control-service.key"))
                
                deleteIfExists (Path.Combine(flockerDir, "flockerctl.crt"))
                deleteIfExists (Path.Combine(flockerDir, "flockerctl.key"))
                let! res = 
                    DockerWrapper.flockerca flockerDir "create-api-certificate flockerctl"
                    |> Proc.startAndAwait
                ()  
            })
            
        let createWorkerConfig dir =
             async { return () }

        let createBaseConfig dir =
            // We always need the docker plugin and an agent...
            initFlocker dir (fun flockerDir ->
              async {
                let! stdOut = 
                    DockerWrapper.flockerca flockerDir "create-node-certificate"
                    |> CreateProcess.ensureExitCode
                    |> Proc.startAndAwait
                let genName = stdOut.Split([| ' ' |]).[1].Split([|'.'|]).[0]
                handle (Path.Combine(flockerDir, sprintf "%s.crt" genName)) (Path.Combine(flockerDir, "node.crt"))
                handle (Path.Combine(flockerDir, sprintf "%s.key" genName)) (Path.Combine(flockerDir, "node.key"))
                
                deleteIfExists (Path.Combine(flockerDir, "plugin.crt"))
                deleteIfExists (Path.Combine(flockerDir, "plugin.key"))
                let! res = 
                    DockerWrapper.flockerca flockerDir "create-api-certificate plugin"
                    |> CreateProcess.ensureExitCode
                    |> Proc.startAndAwait

                ()  
            })
        for i in 1 .. masterNodes do
            let dir = StoragePath.getMasterNodeDir clusterName i
            do! createBaseConfig dir
            do! createMasterConfig dir
            if masterAsWorker then
                do! createWorkerConfig dir
        for i in 1 .. workerNodes do
            let dir = StoragePath.getWorkerNodeDir clusterName i
            do! createBaseConfig dir
            do! createWorkerConfig dir

        
        // Store new secret
        GlobalConfig.readConfig()
        |> GlobalConfig.setSecret clusterName secret
        |> GlobalConfig.writeConfig

        ClusterConfig.setInitialConfig clusterName masterAsWorker

        // save cluster
        Storage.closeCluster clusterName secret
      }

    let provision clusterName (nodeName:string) nodeType =
      async {
        // Setup configuration and stop services
        let hostRoot = "/host"
        if not <| Directory.Exists hostRoot then
            failwith "Make sure the hosts-root filesystem is mounted at /host with '-v /:/host'!"

        do! HostInteraction.stopFlocker()

        if Env.isVerbose then printfn "setup config."
        let flockerDir = Path.Combine (hostRoot, "etc", "flocker")
        if Directory.Exists flockerDir then
            Directory.Delete (flockerDir, true)

        IO.cp { IO.CopyOptions.Default with IsRecursive = true; IntegrateExisting = true }
            (Path.Combine (hostRoot, "yaaf-provision", "machine", "etc", "flocker"))
            (flockerDir)

        IO.chmod IO.CmodOptions.None (LanguagePrimitives.EnumOfValue 0o0700u) flockerDir
        for keyFile in [ "node.key"; "control-service.key"; "plugin.key"; "flockerctl.key" ] do
            let f = Path.Combine(flockerDir, keyFile)
            if File.Exists f then
                IO.chmod IO.CmodOptions.None (LanguagePrimitives.EnumOfValue 0o0600u) f
        
        let flockerPluginDir = Path.Combine(hostRoot, "run", "docker", "plugins", "flocker")
        if Directory.Exists flockerPluginDir then
            Directory.Delete (flockerPluginDir, true)

        do! HostInteraction.installFlocker nodeName nodeType

        if Env.isVerbose then printfn "machine successfully provisioned."
        
        ()
      }


    let init clusterName forceInit =
      async {
        if Env.isVerbose then printfn "starting cluster initialization."
        Storage.openClusterWithStoredSecret clusterName
        let cc = ClusterConfig.readClusterConfig clusterName
        let isInitalized = ClusterConfig.getIsInitialized cc
        if isInitalized then
            if forceInit then
                eprintfn "Cluster is already initialized. Force flag is given, DATA MIGHT BE LOST."
            else
                failwithf "Cluster is already initialized. Use force if you really want to initialize again."

        let masterAsWorker = ClusterConfig.getMasterAsWorker cc
        
        if Env.isVerbose then printfn "checking config."

        CloudProviders.ensureConfig clusterName cc

        
        if Env.isVerbose then printfn "extracting config."
        
        let replacedAgentYml = CloudProviders.getAgentConfig clusterName cc
        
        if Env.isVerbose then printfn "provision nodes."
        let nodeDir = StoragePath.getNodesDir clusterName
        let nodes = Storage.getNodes nodeDir
        let mutable primaryMasterIp = Unchecked.defaultof<_>
        // Create Machine and initialize flocker
        for { Type = t; Dir = dir } in nodes |> Seq.sortBy (fun n -> match n.Type with | Storage.NodeType.PrimaryMaster -> 0 | Storage.NodeType.Master -> 1 | Storage.NodeType.Worker -> 2) do
            let flockerDir = StoragePath.ensureAndReturnDir (Path.Combine (dir, "etc", "flocker"))
            File.WriteAllText (Path.Combine(flockerDir, "agent.yml"), replacedAgentYml)
            
            // create machine
            let nodeName = Storage.getNodeNameByDir dir
            let machine = DockerMachine.getMachineName clusterName nodeName
            
            if Env.isVerbose then printfn "create '%s' docker machine." machine

            let! res = 
                DockerMachine.createProcess clusterName ([|"status"; machine|] |> Arguments.OfArgs)
                |> Proc.startRaw
                |> Async.AwaitTask
            if res.ExitCode = 0 then // already exists
                eprintfn "Machine '%s' seems to be already existing, reusing..." machine
            else
                do! CloudProviders.createMachine clusterName machine cc
                ()

            Storage.quickSaveClusterWithStoredSecret clusterName
        
            // Get IP and ensure networking
            match t with
            | Storage.NodeType.PrimaryMaster ->
                do! CloudProviders.allowInternalNetworking clusterName cc
                let! ip =
                    DockerMachine.getEth0Ip clusterName nodeName
                    |> Proc.startAndAwait
                primaryMasterIp <- ip
            | _ when System.Object.ReferenceEquals(primaryMasterIp, null) ->
                failwith "primary master needs to be initialized first!"
            | _ -> ()
 
            // Write '__CLUSTER_NAME__-master-01' into /etc/hosts
            let hostname = sprintf "%s-master-01" clusterName
            do!
                DockerMachine.createProcess clusterName 
                    (sprintf "ssh %s sudo bash -c \\\"if grep -q '%s' /etc/hosts; then echo master-01 reference exists; else echo '%s %s' >> /etc/hosts; fi\\\"" 
                    machine  hostname primaryMasterIp hostname
                     |> Arguments.OfWindowsCommandLine)
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait

            // provision machine / flocker
            if Env.isVerbose then printfn "upload provision config '%s'." machine
            do!
                DockerMachine.createProcess clusterName 
                    (sprintf "ssh %s sudo rm -rf /yaaf-provision && sudo mkdir /yaaf-provision && sudo chmod 777 /yaaf-provision" machine
                     |> Arguments.OfWindowsCommandLine)
                |> CreateProcess.ensureExitCodeWithMessage "failed to prepare folders"
                |> Proc.startAndAwait
            
            do! DockerMachine.createProcess clusterName (sprintf "scp -r \"%s\" %s:/yaaf-provision/machine" dir machine |> Arguments.OfWindowsCommandLine)
                |> CreateProcess.ensureExitCodeWithMessage "failed to run scp"
                |> Proc.startAndAwait

            // Execute clustermanagement provision --cluster test --nodeName node --nodeType Master
            if Env.isVerbose then printfn "provision '%s'." machine
            let nodeType =
                match t with
                | Storage.NodeType.PrimaryMaster
                | Storage.NodeType.Master when masterAsWorker -> "masterworker"
                | Storage.NodeType.Worker -> "worker"
                | Storage.NodeType.PrimaryMaster
                | Storage.NodeType.Master -> "master"

            // Update tag (useful for development)
            do!
                DockerMachine.createProcess clusterName 
                    (sprintf "ssh %s sudo docker pull %s" machine DockerImages.clusterManagement
                     |> Arguments.OfWindowsCommandLine)
                |> CreateProcess.ensureExitCodeWithMessage "failed to provision machine."
                |> Proc.startAndAwait
            do!
                DockerMachine.createProcess clusterName 
                    (sprintf "ssh %s sudo docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v /:/host %s %s provision --cluster %s --nodename %s --nodetype %s" 
                        machine DockerImages.clusterManagement (if Env.isVerbose then "-v" else "")
                        clusterName nodeName nodeType
                     |> Arguments.OfWindowsCommandLine)
                |> CreateProcess.ensureExitCodeWithMessage "failed to provision machine."
                |> Proc.startAndAwait

        ClusterConfig.setClusterInitialized clusterName true
        Storage.closeClusterWithStoredSecret clusterName

        // From an high level perspective the above is already a fully functionaly cluster, as long as no configuration is required.
        // therefore we should make sure to deploy consul and vault only with high-level functionality available to other software as well
        // like `clustermanagement docker-machine ssh`
        
        // Deploy Swarm
        Deploy.deployIntegrated clusterName "DeploySwarm.fsx"
        
        // Deploy Consul
        Deploy.deployIntegrated clusterName "DeployConsul.fsx"

        // Deploy ClusterManagement
        Deploy.deployIntegrated clusterName "DeployClusterManagement.fsx"

        // Deploy Vault
        Deploy.deployIntegrated clusterName "DeployVault.fsx"
      }
      
    // Kill all containers which block deletion of volumes
    let private killBlockingServices (d:DeployInfo) clusterName =
        async {
        // shutdown all services.
        let! services = 
            DockerWrapper.listServices ()
            |> DockerMachine.runSudoDockerOnNode clusterName "master-01"
            |> Proc.startAndAwait

        for service in services do
            do! DockerWrapper.removeService service.Id
                |> DockerMachine.runSudoDockerOnNode clusterName "master-01"
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait

        for n in d.Nodes do
            let! containers = DockerMachine.runDockerPs clusterName n.Name |> Proc.startAndAwait
            for container in containers do
                let! inspect = DockerMachine.runDockerInspect clusterName n.Name container.ContainerId |> Proc.startAndAwait
                if inspect.Mounts
                    |> Seq.exists (fun m -> m.Driver = Some "flocker") then
                    match inspect.Config.Labels.ComDockerSwarmServiceName with
                    | Some service ->
                        // might have been deleted already (above), but just to be safe...
                        let! res = 
                            DockerWrapper.removeService service
                            |> DockerMachine.runSudoDockerOnNode clusterName n.Name
                            |> CreateProcess.redirectOutput
                            |> Proc.startRaw
                            |> Async.AwaitTask
                        let stdOut = res.Result.Error
                        if stdOut.Contains "not found" then ()
                        else res |> Proc.ensureExitCodeGetResult |> ignore
                    | None ->
                        do! DockerMachine.runDockerKill clusterName n.Name container.ContainerId |> Proc.startAndAwait
                        do! DockerMachine.runDockerRemove clusterName n.Name false container.ContainerId |> Proc.startAndAwait
        }
    let destroy force clusterName =
      async {
        Storage.openClusterWithStoredSecret clusterName
        let cc = ClusterConfig.readClusterConfig clusterName
        let isInit = ClusterConfig.getIsInitialized cc
        if not isInit then
            eprintfn "WARN: Cluster is not initialized. No-op."
        else
            let d = Deploy.getInfoInternal clusterName [||]
            try
                do! killBlockingServices d clusterName
            
                // Clear/Delete volumes -> Sets them to status "deleting"
                let! datasets = Volume.list clusterName
                for d in datasets do
                    do! Volume.destroy clusterName d.Dataset

                // Give flocker some time to act and delete its nodes
                let mutable containsItems = true
                let mutable iter = 0
                let maxIter = 2000
                while containsItems && iter < maxIter do
                    let! items = Volume.list clusterName
                    containsItems <- not items.IsEmpty
                    iter <- iter + 1
                    if containsItems then
                        eprintfn "Give flocker some time to cleanup.., missing:\n%A" items
                        do! Async.Sleep 500
                    if iter % 120 = 0 then
                        eprintfn "try restarting flocker instances"
                        do! HostInteraction.restartFlocker clusterName d.Nodes
                        // again try to kill all services/containers
                        do! killBlockingServices d clusterName
            
                if iter = maxIter then
                    eprintfn "COULD NOT DELETE ALL FLOCKER IMAGES. DATA MIGHT BE LEFT - PLEASE DELETE THEM YOURSELF (FOR EXAMPLE IN YOUR AWS CONSOLE)."
            with e ->
                if force then
                    eprintfn "Error while deleting flocker images: %O" e
                    eprintfn "COULD NOT DELETE ALL FLOCKER IMAGES. DATA MIGHT BE LEFT - PLEASE DELETE THEM YOURSELF (FOR EXAMPLE IN YOUR AWS CONSOLE)."
                else
                    raise <| exn("Could not cleanup flocker images. To continue and force the deletion of the docker machines, use force", e)

            // Clear/Delete docker-machines
            for n in d.Nodes do
                do! DockerMachine.remove force clusterName n.MachineName
                    |> CreateProcess.ensureExitCodeWithMessage "failed to delete a machine!"
                    |> Proc.startAndAwait
            ClusterConfig.setClusterInitialized clusterName false

        Storage.closeClusterWithStoredSecret clusterName
        ()
      }

    let delete clusterName force =
        Storage.openClusterWithStoredSecret clusterName
        let cc = ClusterConfig.readClusterConfig clusterName
        let isInit = ClusterConfig.getIsInitialized cc
        Storage.closeClusterWithStoredSecret clusterName
        if isInit then
            if force then
                eprintfn "WARN: Deleting an initialized cluster!"
            else
                failwithf "This cluster is already initialized. Use --force to delete it anyway."
        
        Storage.deleteCluster clusterName
