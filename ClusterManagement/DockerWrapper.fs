namespace ClusterManagement

module DockerImages =
    let flockerTag = "1.14.0"
    let flockerControlService = "clusterhq/flocker-control-service"
    let flockerDatasetAgent = "clusterhq/flocker-dataset-agent"
    let flockerDockerPlugin = "clusterhq/flocker-dockerplugin"
    let flockerCtl = "clusterhq/uft"
    let clusterManagement = "matthid/clustermanagement"


module DockerWrapper =
    type HostSource =
        | Dir of string
        | NamedVolume of string
        override x.ToString() =
            match x with
            | Dir d -> d
            | NamedVolume n -> n 
    type Mount = { HostSource : HostSource; ContainerDir : string }

    let dockerPath = ref "docker"
    let baseMounts = ref []
    
    let runExt opts args =
        Proc.startProcessCustomizedWithOpts opts (fun p ->
            p |> Proc.defaultCustomize (System.IO.Directory.GetCurrentDirectory()) !dockerPath args
            let setEnv key var =
                p.EnvironmentVariables.[key] <- var
            for entry in System.Environment.GetEnvironmentVariables() |> Seq.cast<System.Collections.DictionaryEntry> do
                setEnv (entry.Key :?> string) (entry.Value :?> string))
    
    let run args = runExt Proc.RedirectOptions.Default args
    let runInteractive args = runExt Proc.RedirectOptions.Interactive args
    
    // otherwise compiler needs inspect-example for projects referencing this assembly :(
    type internal InspectJson = FSharp.Data.JsonProvider< "inspect-example.json" >
    
    let internal getFirstInspectJson json =
        let json = InspectJson.Load(new System.IO.StringReader(json))
        json.[0]

    let ensureWorking() =
      async { 
        do!
            Proc.startProcess !dockerPath "version"
            |> Async.map (Proc.failOnExitCode)
            |> Async.Ignore
        // Find our own container-id and save all binds for later mapping.
        if System.IO.File.Exists("/proc/self/cgroup") then
            let searchStr = ":/docker/"
            let cgroupString = System.IO.File.ReadAllLines("/proc/self/cgroup")
            let dockerId = 
                cgroupString
                |> Seq.choose (fun l -> 
                    let i = l.IndexOf(searchStr)
                    if i >= 0 then
                        Some <| l.Substring(i + searchStr.Length)
                    else None)
                |> Seq.filter (fun s -> s.Length > 0)
                |> Seq.tryHead 
            match dockerId with
            | Some id ->
                Env.isContainerized <- true
                let! stdOut =
                    run (sprintf "inspect %s" id) 
                    |> Async.map (Proc.failOnExitCode)
                    |> Async.map (Proc.getStdOut)
                let first = getFirstInspectJson stdOut
                let binds =
                    first.HostConfig.Binds
                    |> Seq.map (fun m -> 
                        let s = m.Split [|':'|] 
                        let host, container = s.[0], s.[1]
                        let source =
                            if host.StartsWith "/" then HostSource.Dir host else HostSource.NamedVolume host
                        { HostSource = source; ContainerDir = container })
                    |> Seq.toList
                baseMounts := binds
            | None ->
              if Env.isVerbose then
                printfn "Docker-Id not found in /proc/self/cgroup: '%s'" (System.String.Join(System.Environment.NewLine, cgroupString))
        else
          if Env.isVerbose && Env.isLinux then printfn "Could not find /proc/self/cgroup"
      }

    // outer container /c/test/dir/currentDir:/currentDir
    // now map /currentDir/test to /c/test/dir/currentDir
    let mapHostDir (currentDir:string) =
        let matchingMount =
            !baseMounts
            |> Seq.filter (fun m -> currentDir.StartsWith m.ContainerDir)
            |> Seq.sortByDescending (fun m -> m.ContainerDir.Length)
            |> Seq.tryHead
        match matchingMount with
        | Some matchingMount ->
            match matchingMount.HostSource with
            | HostSource.Dir dir ->
                HostSource.Dir <| currentDir.Replace(matchingMount.ContainerDir, dir)
            | HostSource.NamedVolume n ->
                if currentDir = matchingMount.ContainerDir then
                    HostSource.NamedVolume n
                else
                    failwithf "subdirectories of named volumes are not supported. Volume: '%s'" n
        | None ->
            failwithf "cannot use '%s' within docker container, as it is not mapped. Try to map it via '-v /some/dir:%s'" currentDir currentDir

    let mapGivenPath (path:string) =
        if Env.isContainerized then
            if System.IO.Path.IsPathRooted(path) then
                // look into /host
                "/host" + path
            else
                // append to /workDir
                System.IO.Path.Combine("/workDir", path.Replace("\\", "/"))
        else
            path


    let flockerca flockercerts args =
      async {
        let path = System.IO.Path.GetFullPath (flockercerts)
        let args = sprintf "run --rm -v \"%O:/flockercerts\" hugecannon/flocker-cli %s" (mapHostDir path) args
        let! res = run args
        return
            res 
            |> Proc.failOnExitCode
            |> Proc.getStdOut
      }
      
    let flockerctl args =
      async {
        let args = sprintf "run --net=host --rm -e FLOCKER_CERTS_PATH=\"/etc/flocker\" -e FLOCKER_USER=\"flockerctl\" -e FLOCKER_CONTROL_SERVICE=\"${CLUSTER_NAME}-01\" -e CONTAINERIZED=1 -v /:/host -v $PWD:/pwd:z clusterhq/uft:latest flockerctl %s" args
        let! res = run args
        return
            res 
            |> Proc.failOnExitCode
            |> Proc.getStdOut
      }

    let getNodes () =
      async {
        let! res = flockerctl "list-nodes"
        // node_hash=`echo $res | grep 127.0.0.1 | cut -d " " -f 1`

        ()
      }
      
    type DockerServiceReplicas = { Current : int; Requested : int}
    type DockerService =
        { Id : string; Name : string; Mode : string; Replicas : DockerServiceReplicas; Image : string }
    let parseServices (out:string) =
        let splitLine (line:string) =
            let (s:string array) = line.Split ([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
            assert (s.Length = 5)
            if s.Length <> 5 then 
                if s.Length > 5 
                    then eprintfn "Could not parse output line from 'docker service ls': %s" line 
                    else failwithf "Could not parse output line from 'docker service ls': %s" line 
            let (rep:string array) = s.[3].Split([|'/'|])
            if rep.Length <> 2 then 
                if rep.Length > 2
                    then eprintfn "Could not parse output (rep) line from 'docker service ls': %s" line
                    else failwithf "Could not parse output (rep) line from 'docker service ls': %s" line
            let currentRep = 
                match System.Int32.TryParse(rep.[0]) with
                | true, i -> i
                | _ -> failwithf "Could not parse output line (currentRep) from 'docker service ls': %s" line
            let maxRep = 
                match System.Int32.TryParse(rep.[1]) with
                | true, i -> i
                | _ -> failwithf "Could not parse output line (maxRep) from 'docker service ls': %s" line
            { Id = s.[0]; Name = s.[1]; Mode = s.[2]; Replicas = { Current = currentRep; Requested = maxRep }; Image = s.[4] }

        out.Split([| '\r'; '\n' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Seq.skip 1
        |> Seq.map splitLine
        |> Seq.toList

        
    type internal ServiceInspectJson = FSharp.Data.JsonProvider< "service-inspect-example.json" >
    type VirtualIp = { NetworkId : string; Addr : string; NetmaskBits : int }
    type ServiceInspectEndpoint = { VirtualIps : VirtualIp list }
    type ServiceInspect =
        { Id : string
          Endpoint : ServiceInspectEndpoint }
    let getServiceInspectJson json =
        let json = ServiceInspectJson.Load(new System.IO.StringReader(json))
        let inspectRaw = json.[0]
        { Id = inspectRaw.Id
          Endpoint = 
            { VirtualIps = 
                inspectRaw.Endpoint.VirtualIPs 
                |> Seq.map (fun ip -> 
                    let addrSplit = ip.Addr.Split([|'/'|])
                    { NetworkId = ip.NetworkId; Addr = addrSplit.[0]; NetmaskBits = System.Int32.Parse(addrSplit.[1]) }) 
                |> Seq.toList
            }
        }

module DockerMachineWrapper =
    let dockerMachinePath = ref "docker-machine"
    let dockerMachineStoragePath = "/docker-machine/storage"
    let ensureWorking() =
        Proc.startProcess !dockerMachinePath "version"
        |> Async.map (Proc.failOnExitCode)
        |> Async.Ignore

    let runExt opts confDir args =
      async {
        // Safe copy MACHINE_STORAGE_PATH because of a permission issue on windows...
        if System.IO.Directory.Exists dockerMachineStoragePath then
            System.IO.Directory.Delete(dockerMachineStoragePath, true)
        System.IO.Directory.CreateDirectory(dockerMachineStoragePath) |> ignore
        let t = dockerMachineStoragePath

        try
            Env.cp { Env.CopyOptions.Default with IntegrateExisting = true; IsRecursive = true }
                confDir t
            
            Env.chmod Env.CmodOptions.Rec (LanguagePrimitives.EnumOfValue 0o0600u) t

            let! res = Proc.startProcessCustomizedWithOpts opts (fun p ->
                p |> Proc.defaultCustomize (System.IO.Directory.GetCurrentDirectory()) !dockerMachinePath args
                let setEnv key var =
                    p.EnvironmentVariables.[key] <- var
                for entry in System.Environment.GetEnvironmentVariables() |> Seq.cast<System.Collections.DictionaryEntry> do
                    setEnv (entry.Key :?> string) (entry.Value :?> string)
                setEnv "MACHINE_STORAGE_PATH" t)
            System.IO.Directory.Delete(confDir, true)
            Env.cp { Env.CopyOptions.Default with IntegrateExisting = true; IsRecursive = true }
                t confDir
            return res
        finally
            System.IO.Directory.Delete (t, true)
      }

    let run confDir args = runExt Proc.RedirectOptions.Default confDir args
    let runInteractive confDir args = runExt Proc.RedirectOptions.Interactive confDir args


