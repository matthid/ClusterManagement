namespace ClusterManagement

module Volume =
    open System

    type ClusterDockerInfo =
        { SimpleName : string; Cluster : string }

    type ClusterDockerVolume =
        { Info : DockerWrapper.DockerVolume; ClusterInfo : ClusterDockerInfo option }
        member x.SimpleName =
            match x.ClusterInfo with
            | Some c -> c.SimpleName
            | None -> x.Info.Name

    let createFullName cluster (name:string) =
        Config.checkName name
        sprintf "%s_%s" cluster name

    let tryGetClusterInfo (fullName:string) =
        let i = fullName.IndexOf('_')
        if i < 0 then
            None
        else
            let cluster = fullName.Substring(0, i)
            let simpleName = fullName.Substring(i+1)
            Some { SimpleName = simpleName; Cluster = cluster }

    let list cluster =
      async {
        let! res =
            DockerWrapper.listVolumes ()
            |> DockerMachine.runSudoDockerOnNode cluster "master-01"
            |> CreateProcess.map (List.map (fun rawInfo -> { Info = rawInfo; ClusterInfo = tryGetClusterInfo rawInfo.Name }))
            |> Proc.startRaw
        res |> Proc.ensureExitCodeGetResult |> ignore
        return res.Result
      }

    let remove cluster volume =
      async {
        async.Bind(async {return()} |> Async.StartAsTask, fun _ -> async { return () })
            |> ignore
        let! res =
            DockerWrapper.removeVolume volume
            |> DockerMachine.runSudoDockerOnNode cluster "master-01"
            |> Proc.startRaw
        res |> Proc.ensureExitCodeGetResult |> ignore
        return res
      }

    let findVolumeFrom cluster name vols =
        //let fullName = createFullName cluster name
        let globalMatch =
            vols |> Seq.tryFind (fun v -> v.Info.Name = name)
        let clusterMatch =
            vols
            |> Seq.tryFind(fun v -> match v.ClusterInfo with Some s -> s.Cluster = cluster && s.SimpleName = name | _ -> false)
        match globalMatch, clusterMatch with
        | Some g, Some l ->
            eprintfn "Cluster and global volume match name '%s', took the global one (use the full name for the cluster one). Don't do this!" name
            Some g
        | Some g, _ -> Some g
        | _, Some l -> Some l
        | _ -> None

    let findVolume cluster name =
      async {
        let! vols = list cluster
        return findVolumeFrom cluster name vols
      }

    let createEx isGlobal cluster name plugin opts =
      async {
        let pluginInfo = Plugins.getPlugin plugin
        do! Plugins.ensurePluginInstalled cluster plugin
        
        // check if exists
        let! foundVolume = findVolume cluster name

        match foundVolume with
        | Some v ->
            eprintfn "Not creating volume '%s' as it already exists (name: %s, simplename: %s, driver: %s)." name v.Info.Name v.SimpleName v.Info.Driver
            return v
        | None ->
            // docker volume create
            // docker volume create --driver=rexray/ebs --name=test123 --opt=size=0.5
            let fullName = createFullName cluster name
            let volname = if isGlobal then name else fullName
            let! res =
                DockerWrapper.createVolume volname pluginInfo.ImageName opts
                |> DockerMachine.runSudoDockerOnNode cluster "master-01"
                |> Proc.startRaw
            res |> Proc.ensureExitCodeGetResult |> ignore
            let ci =
                if isGlobal then None else Some { SimpleName = name; Cluster = cluster }
            return { Info = { Name = volname; Driver = pluginInfo.ImageName }; ClusterInfo = ci }
      }


    let createEbs isGlobal cluster name (sizeInGb:int64) =
        createEx isGlobal cluster name Plugin.Ebs [("size", sprintf "%d" sizeInGb)]

    [<Obsolete "Please use createEbs or createEx instead. Make sure to update the size (as they use GB as unit)">]
    let create cluster name (size:int64) =
        let sizeInGB = decimal size / 1000000000.0m
        let sizeInGB_rounded = Math.Max(1L, size / 1000000000L)
        if Math.Abs(decimal sizeInGB_rounded - sizeInGB) > 0.0005m then
            eprintfn "rexray accepts only gb, therefore we rounded your value to '%d'gb. To get rid of this warning use a multiple of 1000000000" sizeInGB_rounded
        createEbs false cluster name sizeInGB_rounded

    let createS3fs isGlobal cluster name (sizeInGb:int64) =
        createEx isGlobal cluster name Plugin.S3fs [("size", sprintf "%d" sizeInGb)]

    let copyContents fileName (direction:CopyDirection) cluster volName targetDir =
      async {
        let node = "master-01"
        do!
            DockerWrapper.remove true "backup-volume-helper"
            |> DockerMachine.runSudoDockerOnNode cluster node
            |> Proc.startAndAwait

        // This ensures the flocker volume is mounted on the master node
        // We don't even check if the volume is already mounted because this has two advantages:
        // - This way docker ensures for us to not umount the volume to somewhere else
        // - It already fails if the volume is mounted elsewhere
        // docker run -d --rm --volume-driver flocker -v backup_yaaf-prod-seafile:/backup -v yaaf-prod-seafile:/data --name my_backup-volume-helper --net swarm-net -e NOSTART=true --entrypoint /sbin/my_init phusion/baseimage
        // docker exec -ti my_backup-volume-helper /bin/bash
        let! volInfo =
            DockerWrapper.inspectVolume volName
            |> DockerMachine.runSudoDockerOnNode cluster node
            |> Proc.startAndAwait
        // TODO: resolve volName if not exist -> fullname
        let! containerId =
            DockerWrapper.createProcess
                (sprintf "run -d --rm --volume-driver %s -v %s:/backup --name backup-volume-helper --net swarm-net -e NOSTART=true --entrypoint /sbin/my_init phusion/baseimage"
                    volInfo.Driver
                    volName
                 |> Arguments.OfWindowsCommandLine)
            |> CreateProcess.redirectOutput
            |> CreateProcess.ensureExitCode
            |> CreateProcess.map (fun r -> r.Output.Trim())
            |> DockerMachine.runSudoDockerOnNode cluster node
            |> Proc.startAndAwait

        try
            // Use docker-exec and tar
            let makeRemote (proc:CreateProcess<_>) =
                proc
                |> DockerWrapper.exec containerId
                |> DockerMachine.runSudoDockerOnNode cluster node
                //|> Sudo.wrapCommand
                //|> DockerMachine.sshExt cluster node

            do! DockerMachine.copyContentsExt None makeRemote fileName direction targetDir "/backup"
        finally
            DockerWrapper.remove true containerId
            |> DockerMachine.runSudoDockerOnNode cluster node
            |> Proc.startAndAwait
            |> Async.RunSynchronously
      }

    let download cluster volName targetDir = copyContents "." CopyDirection.Download cluster volName targetDir
    let upload cluster volName targetDir = copyContents "." CopyDirection.Upload cluster volName targetDir

    let clone volume fromCluster toCluster =
      async {
        let volNames =
            match volume with
            | Some vol -> [vol]
            | None -> failwithf "Not jet implemented"
        
        for volName in volNames do
            // On the dest-cluster run the clustermanagement container and mount the target volume
            // use the regular download command :)
            // TODO: Ensure that volName volume exists in 'dest'...
            let! foundVolume = findVolume toCluster volName
            match foundVolume with
            | None -> failwithf "Volume '%s' was not found on destination cluster" volName
            | Some volInfo ->
                do!
                    DockerMachine.createProcess toCluster
                        (sprintf "ssh %s sudo docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v /:/host -v %s:/volume %s %s volume download --cluster %s --volume %s --localfolder /volume"
                            "master-01" DockerImages.clusterManagement 
                            volInfo.Info.Name (if Env.isVerbose then "-v" else "")
                            fromCluster volName
                         |> Arguments.OfWindowsCommandLine)
                    |> CreateProcess.ensureExitCodeWithMessage (sprintf "failed to clone volume '%s'." volName)
                    |> Proc.startAndAwait
        ()
      }
