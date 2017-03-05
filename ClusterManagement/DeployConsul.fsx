﻿#if !CLUSTERMANGEMENT
#r "bin/Debug/ClusterManagement.exe"
#endif

open ClusterManagement

let d = Deploy.getInfo()
if Env.isVerbose then
    printfn "Deploying Consul to cluster '%s'" d.ClusterName

// TODO: Deploy with this.   
// docker service create --name primary-consul --mount type=volume,src=master-01-consul,dst=/consul/data,volume-driver=flocker --network swarm-net --constraint 'node.role == manager' consul agent -server -advertise=10.0.0.2 -bootstrap-expect=1 -bind=0.0.0.0 -client=0.0.0.0
// use inspect on created service -> VirtualIP should be 10.0.0.2...
// docker service create --name consul-master-NN  --mount type=volume,src=master-NN-consul,dst=/consul/data,volume-driver=flocker  --network swarm-net --constraint 'node.role == manager' consul agent -server -advertise=10.0.0.4 -bind=0.0.0.0 -client=0.0.0.0 -retry-join=10.0.0.2
// docker service create --name consul-agent --replicas NN --network swarm-net --constraint 'node.role != manager' consul agent -bind=0.0.0.0 -client=0.0.0.0 -retry-join=10.0.0.2 -retry-join=10.0.0.4

let rawMasterNum = d.Nodes |> Seq.sumBy (fun n -> match n.Type with Storage.NodeType.PrimaryMaster -> 1 | Storage.NodeType.Master -> 1 | Storage.NodeType.Worker -> 0)
let rawWorkerNum =  d.Nodes.Length - rawMasterNum

let masterNum = max rawMasterNum 3
let workerNum = max rawWorkerNum 2

let runDocker args = DockerMachine.runDockerOnNode d.ClusterName "master-01" args |> Async.RunSynchronously

let services = runDocker "service ls" |> Proc.failOnExitCode |> Proc.getStdOut |> DockerWrapper.parseServices

for service in services |> Seq.filter (fun s -> s.Name.StartsWith("consul")) do
    let res = runDocker (sprintf "service rm %s" service.Id)
    if res.ExitCode <> 0 then
        eprintfn "Failed (%d) to remove service %s.\nOutput: %s\nError: %s" res.ExitCode service.Name res.Output.StdOut res.Output.StdErr


for master in [ 1 .. masterNum ] do
    let volName = sprintf "%s-consul-master-%02d" d.ClusterName master
    // CM docker-machine -c <cluster> -- ssh blub-master-01 ifconfig -> get docker0 ip
    Volume.create d.ClusterName volName (1024L * 1024L * 1024L) // 1 GB 
    |> Async.RunSynchronously
    if master = 1 then
        runDocker 
            (sprintf "service create --name consul-master-%02d --mount type=volume,src=%s,dst=/consul/data,volume-driver=flocker --network swarm-net --constraint node.role==manager -e 'CONSUL_LOCAL_CONFIG={\"skip_leave_on_interrupt\":true}' consul agent -server -advertise=10.0.0.3 -bootstrap-expect=%d -bind=0.0.0.0 -client=0.0.0.0"
                master volName masterNum)
            |> Proc.failOnExitCode
            |> ignore
        // for the first node we assert if the ip is correct
        let inspect = runDocker (sprintf "service inspect %s" "consul-master-01") |> Proc.failOnExitCode |> Proc.getStdOut |> DockerWrapper.getServiceInspectJson
        let ip = inspect.Endpoint.VirtualIps.Head
        if ip.Addr <> "10.0.0.2" then failwithf "expected ip 10.0.0.2, but was %s" ip.Addr
    else
        runDocker 
            (sprintf "service create --name consul-master-%02d --mount type=volume,src=%s,dst=/consul/data,volume-driver=flocker --network swarm-net --constraint node.role==manager -e 'CONSUL_LOCAL_CONFIG={\"skip_leave_on_interrupt\":true}' consul agent -server -advertise=10.0.0.%d -retry-join=10.0.0.3 -bootstrap-expect=%d -bind=0.0.0.0 -client=0.0.0.0"
                master volName (1 + master * 2) masterNum)
            |> Proc.failOnExitCode
            |> ignore

let contraint = if rawWorkerNum > 0 then "--constraint node.role!=manager " else ""
runDocker 
    (sprintf "service create %s--name consul --replicas %d --network swarm-net consul agent -advertise=10.0.0.%d -bind=0.0.0.0 -client=0.0.0.0 -retry-join=10.0.0.3 -retry-join=10.0.0.5 -retry-join=10.0.0.7"
        contraint workerNum (3 + masterNum * 2))

// add tokens as config
let mutable retryCount = 10
for tok in Config.getTokens d.ClusterName d.ClusterConfig do
    let mutable success = false
    while not success do
        try
            runDocker
                (sprintf "run --net=swarm-net consul kv put -http-addr=consul:8500 yaaf/config/tokens/%s %s" tok.Name tok.Value)
            |> Proc.failOnExitCode
            |> ignore
            success <- true
        with e when retryCount > 0 ->
            eprintfn "Failed to contact consul server, might be still starting up... waiting some time. error was: %O" e
            retryCount <- retryCount - 1
            System.Threading.Thread.Sleep 500