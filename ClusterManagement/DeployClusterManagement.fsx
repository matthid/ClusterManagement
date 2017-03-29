#if !CLUSTERMANGEMENT
#r "bin/Debug/ClusterManagement.exe"
#endif

open ClusterManagement

let d = Deploy.getInfo()
if Env.isVerbose then
    printfn "Deploying ClusterManagement to cluster '%s'" d.ClusterName

let runDockerRaw node args = DockerMachine.runSudoDockerOnNode d.ClusterName node args |> Proc.startRaw |> fun t -> t.GetAwaiter().GetResult()
let runDockerE node cmd =
    DockerWrapper.createProcess (cmd |> Arguments.OfWindowsCommandLine)
    |> CreateProcess.redirectOutput
    |> runDockerRaw node
let runDocker cmd = runDockerE "master-01" cmd

let volName = sprintf "%s-clustermanagement" d.ClusterName

// Stop existing service
runDocker "service rm clustermanagement"
    |> ignore

// CM docker-machine -c <cluster> -- ssh blub-master-01 ifconfig -> get docker0 ip
Volume.create d.ClusterName volName (1024L * 1024L * 1024L) // 1 GB
|> Async.RunSynchronously

// upload config to the volume
let tmpPath = System.IO.Path.GetTempFileName()
System.IO.File.Delete tmpPath
System.IO.Directory.CreateDirectory tmpPath
try
    let tmpConfig = System.IO.Path.Combine(tmpPath, StoragePath.clusterConfig)
    System.IO.File.Copy (StoragePath.getClusterConfigFile d.ClusterName, tmpConfig)
    // Add CLUSTER_NAME
    ClusterConfig.readConfigFromFile tmpConfig
        |> ClusterConfig.setConfig "CLUSTER_NAME" d.ClusterName
        |> ClusterConfig.writeClusterConfigToFile tmpConfig

    let source = StoragePath.getConfigFilesDir d.ClusterName
    let target = System.IO.Path.Combine(tmpPath, StoragePath.configFilesDirName)
    IO.cp { IO.CopyOptions.Default with DoOverwrite = true; IsRecursive = true } source target
    Volume.copyContents "." CopyDirection.Upload d.ClusterName volName tmpPath
        |> Async.RunSynchronously
finally
    System.IO.Directory.Delete(tmpPath, true)

runDocker
    (sprintf "service create --replicas 1 --name clustermanagement --mount type=volume,src=%s,dst=/workdir,volume-driver=flocker --network swarm-net matthid/clustermanagement internal serveconfig"
        volName)
    |> ignore

