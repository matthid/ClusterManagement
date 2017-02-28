#if !CLUSTERMANGEMENT
#r "bin/Debug/ClusterManagement.exe"
#endif

open ClusterManagement

let d = Deploy.getInfo()
if Env.isVerbose then
    printfn "Deploying ClusterManagement to cluster '%s'" d.ClusterName
    
let runDocker args = DockerMachine.runDockerOnNode d.ClusterName "master-01" args |> Async.RunSynchronously

runDocker "service create --replicas 1 --name clustermanagement --network swarm-net matthid/clustermanagement serveconfig"
    |> ignore