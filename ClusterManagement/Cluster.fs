namespace ClusterManagement

type NodeType =
    | Master
    | Worker
    | MasterWorker

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
            let! res = DockerWrapper.flockerca config (Proc.argvToCommandLine [ "initialize"; clusterName])
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
                let! res = DockerWrapper.flockerca flockerDir (sprintf "create-control-certificate %s" nodeName)
                handle (Path.Combine(flockerDir, sprintf "control-%s.crt" nodeName)) (Path.Combine(flockerDir, "control-service.crt"))
                handle (Path.Combine(flockerDir, sprintf "control-%s.key" nodeName)) (Path.Combine(flockerDir, "control-service.key"))
                
                deleteIfExists (Path.Combine(flockerDir, "flockerctl.crt"))
                deleteIfExists (Path.Combine(flockerDir, "flockerctl.key"))
                let! res = DockerWrapper.flockerca flockerDir "create-api-certificate flockerctl"
                ()  
            })
            
        let createWorkerConfig dir =
             async { return () }

        let createBaseConfig dir =
            // We always need the docker plugin and an agent...
            initFlocker dir (fun flockerDir ->
              async {
                let! stdOut = DockerWrapper.flockerca flockerDir "create-node-certificate"
                let genName = stdOut.Split([| ' ' |]).[1].Split([|'.'|]).[0]
                handle (Path.Combine(flockerDir, sprintf "%s.crt" genName)) (Path.Combine(flockerDir, "node.crt"))
                handle (Path.Combine(flockerDir, sprintf "%s.key" genName)) (Path.Combine(flockerDir, "node.key"))
                
                deleteIfExists (Path.Combine(flockerDir, "plugin.crt"))
                deleteIfExists (Path.Combine(flockerDir, "plugin.key"))
                let! res = DockerWrapper.flockerca flockerDir "create-api-certificate plugin"

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
        let hostRoot = "/host-root"
        if not <| Directory.Exists hostRoot then
            failwith "Make sure the hosts-root filesystem is mounted at /host-root with '-v /:/host-root'!"

        if Env.isVerbose then printfn "stopping services."
        for service in [ "flocker-control-service"; "flocker-control-agent"; "flocker-dataset-agent"; "flocker-docker-plugin" ] do
            let! res = DockerWrapper.run (sprintf "kill %s" service)
            if res.ExitCode <> 0 then
                eprintfn "Failed (%d) to kill container %s.\nOutput: %s\nError: %s" res.ExitCode service res.Output.StdOut res.Output.StdErr

            let! res = DockerWrapper.run (sprintf "rm %s" service)
            if res.ExitCode <> 0 then
                eprintfn "Failed (%d) to remove container %s.\nOutput: %s\nError: %s" res.ExitCode service res.Output.StdOut res.Output.StdErr
                
        if Env.isVerbose then printfn "setup config."
        let flockerDir = Path.Combine (hostRoot, "etc", "flocker")
        if Directory.Exists flockerDir then
            Directory.Delete (flockerDir, true)

        Env.cp { Env.CopyOptions.Default with IsRecursive = true; IntegrateExisting = true }
            (Path.Combine (hostRoot, "yaaf-provision", "machine", "etc", "flocker"))
            (flockerDir)

        Env.chmod Env.CmodOptions.None (LanguagePrimitives.EnumOfValue 0o0700u) flockerDir
        for keyFile in [ "node.key"; "control-service.key"; "plugin.key"; "flockerctl.key" ] do
            let f = Path.Combine(flockerDir, keyFile)
            if File.Exists f then
                Env.chmod Env.CmodOptions.None (LanguagePrimitives.EnumOfValue 0o0600u) f
        
        let flockerPluginDir = Path.Combine(hostRoot, "run", "docker", "plugins", "flocker")
        if Directory.Exists flockerPluginDir then
            Directory.Delete (flockerPluginDir, true)

        let initAsMaster =
            match nodeType with
            | NodeType.MasterWorker
            | NodeType.Master -> true
            | NodeType.Worker -> false
            
        let initAsWorker =
            match nodeType with
            | NodeType.MasterWorker
            | NodeType.Worker -> true
            | NodeType.Master -> false
        if Env.isVerbose then printfn "starting services."
        // Start flocker control service
        if nodeName.EndsWith "master-01" then
            let! res = DockerWrapper.run (sprintf "run --name=flocker-control-volume -v /var/lib/flocker %s:%s true" DockerImages.flockerControlService DockerImages.flockerTag)
            if res.ExitCode <> 0 then
                eprintfn "Failed (%d) to create volume container, it might already exist.\nOutput: %s\nError: %s" res.ExitCode res.Output.StdOut res.Output.StdErr

            let! res = DockerWrapper.run (sprintf "run --restart=always -d --net=host -v /etc/flocker:/etc/flocker --volumes-from=flocker-control-volume --name=flocker-control-service %s:%s" DockerImages.flockerControlService DockerImages.flockerTag)
            res |> Proc.failOnExitCode |> ignore

        // Flocker container agent
        let! res = DockerWrapper.run (sprintf "run --restart=always -d --net=host --privileged -v /flocker:/flocker -v /:/host -v /etc/flocker:/etc/flocker -v /dev:/dev --name=flocker-dataset-agent %s:%s" DockerImages.flockerDatasetAgent DockerImages.flockerTag)
        res |> Proc.failOnExitCode |> ignore

        // flocker docker plugin
        let! res = DockerWrapper.run (sprintf "run --restart=always -d --net=host -v /etc/flocker:/etc/flocker -v /run/docker:/run/docker --name=flocker-docker-plugin %s:%s" DockerImages.flockerDockerPlugin DockerImages.flockerTag)
        res |> Proc.failOnExitCode |> ignore

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
        let tokens = Config.getTokens clusterName cc
        
        if Env.isVerbose then printfn "checking config."

        for n in [ "AWS_ACCESS_KEY_ID"; "AWS_ACCESS_KEY_SECRET"; "AWS_REGION"; "AWS_ZONE" ] do
            Config.ensureConfig clusterName n cc
        let awsRegion = (ClusterConfig.getConfig "AWS_REGION" cc).Value
        
        if Env.isVerbose then printfn "extracting config."
        
        let getReplacedResourceText name =
            let resourceText = Env.getResourceText name
            Config.replaceTokens tokens resourceText
        
        let replacedAgentYml = getReplacedResourceText "agent.yml"
        
        if Env.isVerbose then printfn "provision nodes."
        let nodeDir = StoragePath.getNodesDir clusterName
        let nodes = Storage.getNodes nodeDir 
        // Create Machine and initialize flocker
        for { Type = t; Dir = dir } in nodes do
            let flockerDir = StoragePath.ensureAndReturnDir (Path.Combine (dir, "etc", "flocker"))
            File.WriteAllText (Path.Combine(flockerDir, "agent.yml"), replacedAgentYml)
            
            // create machine
            let nodeName = Storage.getNodeNameByDir dir
            let machine = DockerMachine.getMachineName clusterName nodeName
            
            if Env.isVerbose then printfn "create '%s' docker machine." machine

            let! res = DockerMachine.run clusterName (sprintf "status \"%s\"" machine)
            if res.ExitCode = 0 then // already exists
                eprintfn "Machine '%s' seems to be already existing, reusing..." machine
            else
                let! res = DockerMachine.runInteractive clusterName (sprintf "create --driver amazonec2 --amazonec2-region %s \"%s\"" awsRegion machine)
                res |> Proc.failWithMessage "failed create new machine!" |> ignore
                ()

            Storage.quickSaveClusterWithStoredSecret clusterName
        
            // provision machine / flocker
            if Env.isVerbose then printfn "upload provision config '%s'." machine
            let! res = DockerMachine.runInteractive clusterName (sprintf "ssh %s sudo rm -rf /yaaf-provision && sudo mkdir /yaaf-provision && sudo chmod 777 /yaaf-provision" machine)
            res |> Proc.failWithMessage "failed to prepare folders" |> ignore
            
            let! res = DockerMachine.runInteractive clusterName (sprintf "scp -r \"%s\" %s:/yaaf-provision/machine" dir machine)
            res |> Proc.failWithMessage "failed to run scp" |> ignore

            // Execute clustermanagement provision --cluster test --nodeName node --nodeType Master
            if Env.isVerbose then printfn "provision '%s'." machine
            let nodeType =
                match t with
                | Storage.NodeType.PrimaryMaster
                | Storage.NodeType.Master when masterAsWorker -> "masterworker"
                | Storage.NodeType.Worker -> "worker"
                | Storage.NodeType.PrimaryMaster
                | Storage.NodeType.Master -> "master"
            let! res = 
                DockerMachine.runInteractive clusterName 
                    (sprintf "ssh %s sudo docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v /:/host-root %s %s provision --cluster %s --nodename %s --nodetype %s" 
                    machine DockerImages.clusterManagement (if Env.isVerbose then "-v" else "") clusterName nodeName nodeType)
            res |> Proc.failWithMessage "failed to provision machine." |> ignore

        ClusterConfig.setClusterInitialized clusterName true
        Storage.closeClusterWithStoredSecret clusterName

        // From an high level perspective the above is already a fully functionaly cluster, as long as no configuration is required.
        // therefore we should make sure to deploy consul and vault only with high-level functionality available to other software as well
        // like `clustermanagement docker-machine ssh`
        
        // Deploy Swarm
        Deploy.deployIntegrated clusterName "DeploySwarm.fsx"

        // Deploy Consul
        Deploy.deployIntegrated clusterName "DeployConsul.fsx"

        // Deploy Vault
        Deploy.deployIntegrated clusterName "DeployVault.fsx"
      }

    let destroy clusterName =
      async {
        Storage.openClusterWithStoredSecret clusterName
        let cc = ClusterConfig.readClusterConfig clusterName
        let isInit = ClusterConfig.getIsInitialized cc
        if not isInit then
            eprintfn "WARN: Cluster is not initialized. No-op."
        else
            // Clear/Delete volumes -> Sets them to status "deleting"
            let! datasets = Volume.list clusterName
            for d in datasets do
                do! Volume.destroy clusterName d.Dataset
            
            // Kill all containers which block deletion of volumes
            let d = Deploy.getInfoInternal clusterName [||]
            for n in d.Nodes do
                let! containers = DockerMachine.runDockerPs clusterName n.Name
                for container in containers do
                    let! inspect = DockerMachine.runDockerInspect clusterName n.Name container.ContainerId
                    if inspect.Mounts
                       |> Seq.exists (fun m -> m.Driver = Some "flocker") then
                        do! DockerMachine.runDockerKill clusterName n.Name container.ContainerId
                        do! DockerMachine.runDockerRemove clusterName n.Name container.ContainerId
            
            // Give flocker some time to act and delete its nodes
            let mutable containsItems = true
            while containsItems do
                let! items = Volume.list clusterName
                containsItems <- not items.IsEmpty
                if containsItems then
                    eprintfn "Give flocker some time to cleanup..."
                    do! Async.Sleep 500

            // Clear/Delete docker-machines
            for n in d.Nodes do
                let! res = DockerMachine.run clusterName (sprintf "rm -y \"%s\"" n.MachineName)
                res |> Proc.failWithMessage "failed to delete a machine!" |> ignore
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
