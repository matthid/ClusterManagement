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
        if name.Contains "_" then
            failwith "Name cannot contain '_'"
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
        let! res = 
            DockerWrapper.removeVolume volume
            |> DockerMachine.runSudoDockerOnNode cluster "master-01"
            |> Proc.startRaw
        res |> Proc.ensureExitCodeGetResult |> ignore
        return res
      }
    
    let findVolumeFrom cluster name vols =
        let fullName = createFullName cluster name
        let globalMatch =
            vols |> Seq.tryFind (fun v -> v.Info.Name = fullName)
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

    let create isGlobal cluster name (size:int64) =
      async {
        // check if exists
        // docker volume create --driver=rexray/ebs --name=test123 --opt=size=0.5
        let! foundVolume = findVolume cluster name

        match foundVolume with
        | Some v ->
            eprintfn "Not creating volume '%s' as it already exists (name: %s, simplename: %s, driver: %s)." name v.Info.Name v.SimpleName v.Info.Driver
            return v
        | None ->
            // docker volume create 
            let sizeInGB = decimal size / 1000000000.0m
            // docker volume create --driver=rexray/ebs --name=test123 --opt=size=0.5
            let fullName = createFullName cluster name
            let volname = if isGlobal then name else fullName
            let! res = 
                DockerWrapper.createVolume volname "rexray/ebs" [("size", sprintf "%M" sizeInGB)]
                |> DockerMachine.runSudoDockerOnNode cluster "master-01"
                |> Proc.startRaw
            res |> Proc.ensureExitCodeGetResult |> ignore
            let ci =
                if isGlobal then None else Some { SimpleName = name; Cluster = cluster }
            return { Info = { Name = volname; Driver = "rexray/ebs" }; ClusterInfo = ci }
      }

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
        // docker run -d --rm --volume-driver flocker -v backup_yaaf-prod-seafile:/backup -v yaaf-prod-seafile:/data --name my_backup-volume-helper --net swarm-net -e NOSTART=true --entrypoint /sbin/my_init phusion/baseimage
        // docker exec -ti my_backup-volume-helper /bin/bash
        let! volInfo =
            DockerWrapper.inspectVolume volName
            |> DockerMachine.runSudoDockerOnNode cluster node
            |> Proc.startAndAwait
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
            let! parsed =
                DockerWrapper.inspect containerId
                |> DockerMachine.runSudoDockerOnNode cluster node
                |> Proc.startAndAwait

            // find the mountpoint of the docker volume
            match parsed.Mounts |> List.filter (fun m -> m.Driver = Some volInfo.Driver && m.Name = Some volName) with
            | [ mount ] ->
                let flockerDir = mount.Source
                do! DockerMachine.copyContents fileName direction cluster node targetDir flockerDir
            | _ -> failwithf "expected our dummy container to have exactly one mount, but has %A" parsed.Mounts

        finally
            DockerWrapper.remove true containerId
            |> DockerMachine.runSudoDockerOnNode cluster node
            |> Proc.startAndAwait
            |> Async.RunSynchronously
      }

    let download cluster volName targetDir = copyContents "." CopyDirection.Download cluster volName targetDir
    let upload cluster volName targetDir = copyContents "." CopyDirection.Upload cluster volName targetDir

    let clone fromCluster toCluster =
        ()
