namespace ClusterManagement

open Yaaf.FSharp.Scripting

type NodeInfo =
  { Type : Storage.NodeType
    Dir : string
    Name : string
    MachineName : string }

type DeployInfo =
  { ClusterName : string
    ClusterConfig : ClusterConfig.MyClusterConfig
    GlobalConfig : GlobalConfig.MyManagementConfig
    Args : string array
    Nodes : NodeInfo list }

module Deploy =
    let private assembly = System.Reflection.Assembly.GetExecutingAssembly()
    let private assemblyDir = System.IO.Path.GetDirectoryName assembly.Location

    let private session =
        lazy
            let s = ScriptHost.CreateNew(defines = ["CLUSTERMANGEMENT"])
            s.Include(System.IO.Path.GetDirectoryName assembly.Location)
            s.Reference(System.IO.Path.GetFileName assembly.Location)
            s

    let mutable private info = Unchecked.defaultof<_>

    let getInfo () =
        let i = info
        if System.Object.ReferenceEquals(i, null) then
            failwith "this script must be run with 'ClusterManagement.exe deploy --script script.fsx'"
        i

    let internal getInfoInternal cluster args =
        let cc = ClusterConfig.readClusterConfig cluster
        let gc = GlobalConfig.readConfig()
        let nodeDir = StoragePath.getNodesDir cluster
        let nodes =
            Storage.getNodes nodeDir
            |> Seq.map (fun n ->
                let nodeName = Storage.getNodeNameByDir n.Dir
                let machine = DockerMachine.getMachineName cluster nodeName
                { Dir = n.Dir
                  Name = nodeName
                  MachineName = machine
                  Type = n.Type })
            |> Seq.toList
        { ClusterName = cluster; ClusterConfig = cc; GlobalConfig = gc; Args = args; Nodes = nodes }

    let deploy cluster scriptFile args =
      //async {
        let session = session.Force()
        //let scriptDir = System.IO.Path.GetDirectoryName scriptFile
        //let name = System.IO.Path.GetFileName scriptFile
        //let targetPath = System.IO.Path.Combine(assemblyDir, name)
        let fullScriptPath = System.IO.Path.GetFullPath scriptFile
        //if targetPath <> fullScriptPath then
        //    System.IO.File.Copy(fullScriptPath, targetPath, true)

        Storage.openClusterWithStoredSecret cluster

        info <- getInfoInternal cluster args

        session.Load (fullScriptPath)

        info <- Unchecked.defaultof<_>

        Storage.closeClusterWithStoredSecret cluster
      //}

    let deployIntegrated cluster file =
        let t = IO.getResourceText file
        let targetPath = System.IO.Path.Combine(assemblyDir, file)
        System.IO.File.WriteAllText(targetPath, t)
        deploy cluster targetPath [||]