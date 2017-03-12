#if !CLUSTERMANGEMENT
#r "bin/Debug/ClusterManagement.exe"
#endif

open ClusterManagement

let d = Deploy.getInfo()
if Env.isVerbose then
    printfn "Deploying ClusterManagement to cluster '%s'" d.ClusterName
    
let runDocker args = DockerMachine.runDockerOnNode d.ClusterName "master-01" args |> Async.RunSynchronously

runDocker "service create --replicas 1 --name clustermanagement --network swarm-net matthid/clustermanagement internal serveconfig"
    |> ignore

// upload config files
let machineName = DockerMachine.getMachineName d.ClusterName "master-01"
DockerMachine.runInteractive d.ClusterName 
    (sprintf "scp -r \"%s\" %s:/yaaf-provision/config" (StoragePath.getConfigFilesDir d.ClusterName) machineName)
    |> Async.RunSynchronously
    |> Proc.failWithMessage "failed to run scp"
    |> ignore
let mutable retryCount = 120
let mutable success = false
while not success do
    try
        runDocker
            (sprintf "run --net=swarm-net -v /yaaf-provision/config:/config matthid/clustermanagement internal deployconfig")
        |> Proc.failOnExitCode
        |> ignore
        success <- true
    with e when retryCount > 0 ->
        eprintfn "Failed to contact consul server, might be still starting up... waiting some time. error was: %O" e
        retryCount <- retryCount - 1
        System.Threading.Thread.Sleep 1000
