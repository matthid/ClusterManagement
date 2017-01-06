#if !CLUSTERMANGEMENT
#r "bin/Debug/ClusterManagement.exe"
#endif

open ClusterManagement

let d = Deploy.getInfo()
if Env.isVerbose then
    printfn "Deploying Consul to cluster '%s'" d.ClusterName

let mutable primaryMasterIp = Unchecked.defaultof<_>
let masterNum = d.Nodes |> Seq.sumBy (fun n -> match n.Type with Storage.NodeType.PrimaryMaster -> 1 | Storage.NodeType.Master -> 1 | Storage.NodeType.Worker -> 0)
for n in d.Nodes |> Seq.sortBy (fun n -> match n.Type with Storage.NodeType.PrimaryMaster -> 0 | Storage.NodeType.Master -> 1 | Storage.NodeType.Worker -> 2) do
    // CM volume create -c <cluster> --name master-NUM-consul
    //
    
    // For each master -> Create Flocker Volume
    let volName = sprintf "%s-consul" n.MachineName
    let isMaster =
        match n.Type with
        | Storage.NodeType.PrimaryMaster -> true
        | Storage.NodeType.Master -> true
        | Storage.NodeType.Worker -> false
    if isMaster then
        // CM docker-machine -c <cluster> -- ssh blub-master-01 ifconfig -> get docker0 ip
        Volume.create d.ClusterName volName (1024 * 1024 * 1024) // 1 GB 
        |> Async.RunSynchronously
        ()

    let runDocker args = DockerMachine.runDockerOnNode d.ClusterName n.Name args |> Async.RunSynchronously
    // Kill existing consul-agent
    for service in [ "consul-agent"] do
        let res = runDocker (sprintf "kill %s" service)
        if res.ExitCode <> 0 then
            eprintfn "Failed (%d) to kill container %s.\nOutput: %s\nError: %s" res.ExitCode service res.Output.StdOut res.Output.StdErr

        let res = runDocker (sprintf "rm %s" service)
        if res.ExitCode <> 0 then
            eprintfn "Failed (%d) to remove container %s.\nOutput: %s\nError: %s" res.ExitCode service res.Output.StdOut res.Output.StdErr
            

    let ip = DockerMachine.getDockerIp d.ClusterName n.Name |> Async.RunSynchronously
    match n.Type with
    | Storage.NodeType.PrimaryMaster ->
        // First master -> docker run -d -v master-NUM-consol:/consul/data --net=host consul agent -server -advertise=<docker0 ip> -bootstrap-expect=master_num
        primaryMasterIp <- ip
        let res = 
            runDocker
                (sprintf "run -d --name=consul-agent --volume-driver flocker -v %s:/consul/data --net=host consul agent -server -advertise=%s -bootstrap-expect=%d"
                volName ip masterNum)
        res |> Proc.failOnExitCode |> ignore
        ()
    | Storage.NodeType.Master ->
        // Other masters -> docker run -d -v master-NUM-consol:/consul/data --net=host consul agent -server -advertise=<docker0 ip> retry-join=<docker0 ip of first master>
        let res = 
            runDocker
                (sprintf "run -d --name=consul-agent --volume-driver flocker -v %s:/consul/data --net=host consul agent -server -advertise=%s -retry-join=%s"
                volName ip primaryMasterIp)
        res |> Proc.failOnExitCode |> ignore
        ()
    | Storage.NodeType.Worker ->
        // other nodes -> docker run -d --net=host consul agent -bind=<docker0 ip> -retry-join=<docker0 ip of first master>
        let res = 
            runDocker
                (sprintf "run -d --name=consul-agent --net=host consul agent -advertise=%s -retry-join=%s"
                ip primaryMasterIp)
        res |> Proc.failOnExitCode |> ignore
        ()
