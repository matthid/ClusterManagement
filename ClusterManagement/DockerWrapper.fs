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
            let searchStr = "cpu:/docker/"
            let dockerId = 
                System.IO.File.ReadAllLines("/proc/self/cgroup")
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
            | None -> ()
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


