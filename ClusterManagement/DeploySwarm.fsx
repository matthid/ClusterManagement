#if !CLUSTERMANGEMENT
#r "bin/Debug/ClusterManagement.exe"
#endif

open ClusterManagement

let d = Deploy.getInfo()
if Env.isVerbose then
    printfn "Deploying Docker-Swarm to cluster '%s'" d.ClusterName
    
let mutable primaryMasterIp = Unchecked.defaultof<_>
let mutable primaryMasterWorkerToken = Unchecked.defaultof<_>
let mutable primaryMasterManagerToken = Unchecked.defaultof<_>
let masterNum = d.Nodes |> Seq.sumBy (fun n -> match n.Type with Storage.NodeType.PrimaryMaster -> 1 | Storage.NodeType.Master -> 1 | Storage.NodeType.Worker -> 0)

// Kill current swarm services
let runDockerRaw node args = DockerMachine.runSudoDockerOnNode d.ClusterName node args |> Proc.startRaw |> fun t -> t.GetAwaiter().GetResult()
let runDockerE node cmd =
    DockerWrapper.createProcess (cmd |> Arguments.OfWindowsCommandLine)
    |> CreateProcess.redirectOutput
    |> runDockerRaw node
let runDocker cmd = runDockerE "master-01" cmd
let res = runDocker "service ls -q"
if res.ExitCode <> 0 then
    eprintfn "Failed (%d) to get existing services from cluster %s.\nOutput: %s\nError: %s" res.ExitCode d.ClusterName res.Result.Output res.Result.Error
else
    // Stop services
    for service in res.Result.Output.Split([|'\r'; '\n'|], System.StringSplitOptions.RemoveEmptyEntries) do
        let res = runDocker (sprintf "service rm %s" service)
        if res.ExitCode <> 0 then
            eprintfn "Failed (%d) to remove service (%s) from cluster %s.\nOutput: %s\nError: %s" res.ExitCode service d.ClusterName res.Result.Output res.Result.Error

for n in d.Nodes |> Seq.sortBy (fun n -> match n.Type with Storage.NodeType.PrimaryMaster -> 0 | Storage.NodeType.Master -> 1 | Storage.NodeType.Worker -> 2) do

    let runDocker cmd = runDockerE n.Name cmd
    // leave existing swarm
    let res = runDocker "swarm leave --force "
    if res.ExitCode <> 0 then
        eprintfn "Failed (%d) to leave swarm cluster, machine: %s.\nOutput: %s\nError: %s" res.ExitCode n.Name res.Result.Output res.Result.Error

    let ip = DockerMachine.getEth0Ip d.ClusterName n.Name |> Proc.startAndAwait |> Async.RunSynchronously
    match n.Type with
    | Storage.NodeType.PrimaryMaster ->
        // First master
        let res = 
            runDocker (sprintf "swarm init --advertise-addr %s" ip)
            |> Proc.ensureExitCodeGetResult
        
        let managerToken =
            runDocker "swarm join-token -q manager"
            |> Proc.ensureExitCodeGetResult
            |> fun r -> r.Output
        let workerToken =
            runDocker "swarm join-token -q worker"
            |> Proc.ensureExitCodeGetResult
            |> fun r -> r.Output
        primaryMasterIp <- ip
        primaryMasterManagerToken <- managerToken
        primaryMasterWorkerToken <- workerToken
        ()
    | Storage.NodeType.Master ->
        let res = 
            runDocker
                (sprintf "swarm join --token %s %s"
                primaryMasterManagerToken primaryMasterIp)
        res |> Proc.ensureExitCodeGetResult |> ignore
        ()
    | Storage.NodeType.Worker ->
        let res = 
            runDocker
                (sprintf "swarm join --token %s %s"
                primaryMasterWorkerToken primaryMasterIp)
        res |> Proc.ensureExitCodeGetResult |> ignore
        ()
  
runDocker "network create --subnet 10.0.0.0/24 --driver overlay --attachable --opt encrypted swarm-net"
    |> ignore


//TODO: Test if current configuration 'just works' (consul node on every machine)
// if not -> deploy as swarm (see above)


