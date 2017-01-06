

#r "bin/Debug/ClusterManagement.exe"

open ClusterManagement
StoragePath.storagePath := "C:\PROJ\Yaaf-Backend\clustercgf\.cm"
StoragePath.tempStoragePath := "C:\PROJ\Yaaf-Backend\clustercgf\.cm-temp"
Env.isVerbose <- true
let cluster = "blub"
Storage.openClusterWithStoredSecret cluster

let exec f =
    let res = f () |> Async.RunSynchronously
    Proc.failOnExitCode res |> ignore
    res.Output
    
printfn "%s" <| (exec (fun () -> Volume.flockerctl cluster "master-01" "--help")).StdOut
printfn "%s" <| (exec (fun () -> DockerMachine.runOnNode cluster "master-01" "sudo docker ps")).StdOut

Storage.closeClusterWithStoredSecret cluster