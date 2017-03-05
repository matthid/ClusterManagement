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
let runDocker args = DockerMachine.runDockerOnNode d.ClusterName "master-01" args |> Async.RunSynchronously
let res = runDocker "service ls -q"
if res.ExitCode <> 0 then
    eprintfn "Failed (%d) to get existing services from cluster %s.\nOutput: %s\nError: %s" res.ExitCode d.ClusterName res.Output.StdOut res.Output.StdErr
else
    // Stop services
    for service in res.Output.StdOut.Split([|'\r'; '\n'|], System.StringSplitOptions.RemoveEmptyEntries) do
        let res = runDocker (sprintf "service rm %s" service)
        if res.ExitCode <> 0 then
            eprintfn "Failed (%d) to remove service (%s) from cluster %s.\nOutput: %s\nError: %s" res.ExitCode service d.ClusterName res.Output.StdOut res.Output.StdErr

for n in d.Nodes |> Seq.sortBy (fun n -> match n.Type with Storage.NodeType.PrimaryMaster -> 0 | Storage.NodeType.Master -> 1 | Storage.NodeType.Worker -> 2) do

    let runDocker args = DockerMachine.runDockerOnNode d.ClusterName n.Name args |> Async.RunSynchronously
    // leave existing swarm
    let res = runDocker "swarm leave --force "
    if res.ExitCode <> 0 then
        eprintfn "Failed (%d) to leave swarm cluster, machine: %s.\nOutput: %s\nError: %s" res.ExitCode n.Name res.Output.StdOut res.Output.StdErr

    let ip = DockerMachine.getEth0Ip d.ClusterName n.Name |> Async.RunSynchronously
    match n.Type with
    | Storage.NodeType.PrimaryMaster ->
        // First master
        let res = 
            runDocker (sprintf "swarm init --advertise-addr %s" ip)
            |> Proc.failOnExitCode
        
        let managerToken =
            runDocker "swarm join-token -q manager"
            |> Proc.failOnExitCode
            |> Proc.getStdOut
        let workerToken =
            runDocker "swarm join-token -q worker"
            |> Proc.failOnExitCode
            |> Proc.getStdOut
        primaryMasterIp <- ip
        primaryMasterManagerToken <- managerToken
        primaryMasterWorkerToken <- workerToken
        ()
    | Storage.NodeType.Master ->
        let res = 
            runDocker
                (sprintf "swarm join --token %s %s"
                primaryMasterManagerToken primaryMasterIp)
        res |> Proc.failOnExitCode |> ignore
        ()
    | Storage.NodeType.Worker ->
        let res = 
            runDocker
                (sprintf "swarm join --token %s %s"
                primaryMasterWorkerToken primaryMasterIp)
        res |> Proc.failOnExitCode |> ignore
        ()
  
runDocker "network create --subnet 10.0.0.0/24 --driver overlay --attachable --opt encrypted swarm-net"
    |> ignore
//let res = runDocker "service create --replicas 1 --name clustermanagement --network swarm-net matthid/clustermanagement serveconfig"

//TODO: Test if current configuration 'just works' (consul node on every machine)
// if not -> deploy as swarm (see above)


