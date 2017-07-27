namespace ClusterManagement

module Env =
    let mutable isVerbose = false
    let mutable isContainerized = false
    let userInterface = System.Environment.UserInteractive
    let isConsoleApp = System.Console.OpenStandardInput(1) <> System.IO.Stream.Null
    let isLinux =
        let p = int System.Environment.OSVersion.Platform
        (p = 4) || (p = 6) || (p = 128)
    let isMono =
        not (isNull <| System.Type.GetType ("Mono.Runtime"))
