﻿namespace ClusterManagement

open System
open System.IO
open FSharp.Configuration

/// Manage a specific set of storage
/// /.cm (add to source control)
///     /cluster1.cluster (ziped and encrypted)
///     /cluster2.cluster
/// /.cm-temp (ignore / only have locally)
///          /cm-config.yml (secrets for decrypted clusters and global local configuration)
///          /cluster1 (folder for temporary cluster operations, decrypted contents of cluster1.cluster)
///                   /docker-machine (configuration of docker-machine)
///                   /cluster-config.yml (cluster specific configuration)
///                   /global (cluster specific global files)
///                          /cluster.key (example: cluster certificate, for flocker)
///                   /nodes (node specific configurations)
///                         /master01
///                         /master02
///                         /worker01
///                         /worker02
module StoragePath =
    let extension = ".cluster"
    let configStorage = "cm-config.yml"
    let clusterConfig = "cluster-config.yml"
    let storagePath =
        let env = Environment.GetEnvironmentVariable "CM_STORAGE"
        ref (if String.IsNullOrEmpty env then ".cm" else env)
    let tempStoragePath =
        let env = Environment.GetEnvironmentVariable "CM_TEMP_STORAGE"
        ref (if String.IsNullOrEmpty env then ".cm-temp" else env)
    let ensureAndReturnDir p =
        if Directory.Exists p |> not then
            Directory.CreateDirectory p |> ignore
        p
    
    let getClusterDirectory name =
        Path.Combine (!tempStoragePath, name)
        |> ensureAndReturnDir

        
    let getDockerMachineDir name =
        Path.Combine(getClusterDirectory name, "docker-machine")
        |> ensureAndReturnDir

    let getClusterConfigFile name =
        Path.Combine(getClusterDirectory name, clusterConfig)
        
    let getGlobalConfigDir name =
        Path.Combine(getClusterDirectory name, "global")
        |> ensureAndReturnDir
        
    let getNodesDir name =
        Path.Combine(getClusterDirectory name, "nodes")
        |> ensureAndReturnDir

    let getMasterNodeDir name num =
        Path.Combine(getNodesDir name, sprintf "master-%02i" num)
        |> ensureAndReturnDir

    let getWorkerNodeDir name num =
        Path.Combine(getNodesDir name, sprintf "worker-%02i" num)
        |> ensureAndReturnDir
        
    let getClusterFile name =
        Path.Combine (!storagePath |> ensureAndReturnDir, sprintf "%s%s" name extension)

    let configPath () = Path.Combine (!tempStoragePath, configStorage)

module GlobalConfig =
    open StoragePath
        
    // Make config 'immutable' for higher abstractions
    type private ClusterManagementConfig = YamlConfig<"cm-config.yml">
    type MyManagementConfig =
        private { ClusterManagementConfig : ClusterManagementConfig }

    let readConfig () =
        let c = configPath()
        let cc = ClusterManagementConfig()

        if File.Exists c then
            cc.Load c
        
        { ClusterManagementConfig = cc }

    let writeConfig { ClusterManagementConfig = cc } =
        let c = configPath()
        cc.Save(c)

    let readSecret name { ClusterManagementConfig = cc } =
        cc.secrets
        |> Seq.tryFind (fun c -> c.clustername = name)
        |> Option.map (fun c -> c.secret)

    let private cloneConfig (cc:ClusterManagementConfig) =
        let ccp = new ClusterManagementConfig()
        let sb = new System.Text.StringBuilder()
        let writer = new StringWriter(sb)
        cc.Save(writer)
        writer.Flush()
        let text = sb.ToString()

        ccp.LoadText(text)
        ccp
        
    let setSecret name newSecret { ClusterManagementConfig = cc } =
        let ccp = cloneConfig cc
        let newItem =
            let i = new ClusterManagementConfig.secrets_Item_Type()
            i.clustername <- name
            i.secret <- newSecret
            i
            
        let filtered =
            ccp.secrets 
            |> Seq.filter (fun c -> c.clustername <> name)
            |> Seq.toList
        ccp.secrets.Clear()
        filtered |> Seq.iter (ccp.secrets.Add)
        ccp.secrets.Add newItem

        { ClusterManagementConfig = ccp }


module ClusterConfig =
    open StoragePath
            
    type private ClusterConfig = YamlConfig<"cluster-config.yml">
    type MyClusterConfig =
        private { ClusterConfig : ClusterConfig }
        
    let private readClusterConfigI allowInitial name =
        let c = getClusterConfigFile name

        let cc = ClusterConfig()
        //cc.config.Clear()
        if File.Exists c then
            cc.Load c
        elif not allowInitial then
            failwith "Cluster needs to be opened/created first"
        
        { ClusterConfig = cc }
           
    let readClusterConfig name =
        readClusterConfigI false name

    let setInitialConfig name masterAsWorker =
        let c = getClusterConfigFile name
        let { ClusterConfig = cc } = readClusterConfigI true name
        cc.globalConfig.masterAsWorker <- masterAsWorker
        cc.Save(c)
        

    let getMasterAsWorker { ClusterConfig = cc } =
        cc.globalConfig.masterAsWorker
        
    let setClusterInitialized name isInit =
        let c = getClusterConfigFile name
        let { ClusterConfig = cc } = readClusterConfig name
        cc.globalConfig.isInitialized <- isInit
        cc.Save(c)
        
    let getIsInitialized { ClusterConfig = cc } =
        cc.globalConfig.isInitialized

    let writeClusterConfig name { ClusterConfig = cc } =
        let c = getClusterConfigFile name
        cc.Save(c)
        
    let private cloneClusterConfig (cc:ClusterConfig) =
        let ccp = new ClusterConfig()
        let sb = new System.Text.StringBuilder()
        let writer = new StringWriter(sb)
        cc.Save(writer)
        writer.Flush()
        let text = sb.ToString()

        ccp.LoadText(text)
        ccp

    let setConfig name value { ClusterConfig = cc } =
        let ccp = cloneClusterConfig cc
        let newItem =
            let i = new ClusterConfig.config_Item_Type()
            i.name <- name
            i.value <- value
            i
        let filtered =
            ccp.config 
            |> Seq.filter (fun c -> c.name <> name)
            |> Seq.toList
        ccp.config.Clear()
        filtered |> Seq.iter (ccp.config.Add)
        ccp.config.Add newItem
        { ClusterConfig = ccp }

    let getConfig name { ClusterConfig = cc } =
        cc.config
        |> Seq.tryFind (fun c -> c.name = name)
        |> Option.map (fun c -> c.value)

    type Token =
        { Name : string; Value : string }
    let getTokens { ClusterConfig = cc } =
        let regular =
            cc.config
            |> Seq.map (fun p -> { Name = p.name; Value = p.value})
        regular
        //let wellKnown =
        //    cc.globalConfig.GetType().GetProperties()
        //    |> Seq.map (fun p -> { Name = p.Name; Value = p.GetValue(cc.globalConfig).ToString()})
        //Seq.append wellKnown regular


module Storage =
    open StoragePath

    let getNodeNameByDir dir =
        Path.GetFileName dir
    type NodeType =
        | Master
        | PrimaryMaster
        | Worker
    type Node =
        { Type : NodeType; Dir : string }
    let getNodes nodesDir =
        Directory.GetDirectories nodesDir
        |> Seq.choose (fun subDir ->
            let name = Path.GetFileName subDir
            let t = 
                if name.StartsWith "master-01" then
                    Some PrimaryMaster
                elif name.StartsWith "master-" then
                    Some Master 
                elif name.StartsWith "worker-" then
                    Some Worker
                else None
            t |> Option.map (fun t -> {Type = t; Dir = subDir}))

    let withDefaultPassword s =
        match s with
        | Some p -> p
        | None -> "secret"

    let openCluster name password =
        Zip.decryptAndUnzip (getClusterDirectory name) password (getClusterFile name)

    let isClusterAvailable name =
        File.Exists (getClusterFile name)

    let closeCluster name password =
        let dir = getClusterDirectory name
        Zip.zipAndEncrypt (getClusterFile name) password dir
        Directory.Delete(dir, true)
    
    let openClusterWithStoredSecret cluster =
        let secret =
            GlobalConfig.readConfig()
            |> GlobalConfig.readSecret cluster
        match secret with
        | Some s ->
            openCluster cluster s
        | None ->
            failwithf "For cluster '%s' no stored secret was found. Please use decrypt to store one." cluster

    let closeClusterWithStoredSecret cluster =
        let secret =
            GlobalConfig.readConfig()
            |> GlobalConfig.readSecret cluster
        match secret with
        | Some s ->
            closeCluster cluster s
        | None ->
            failwithf "For cluster '%s' no stored secret was found. Please use decrypt to store one." cluster

    let quickSaveClusterWithStoredSecret name =
        closeClusterWithStoredSecret name
        openClusterWithStoredSecret name
    
    let deleteCluster cluster =
        let dir = getClusterDirectory cluster
        let file = getClusterFile cluster
        if Directory.Exists dir then
            Directory.Delete(dir, true)
        
        if File.Exists file then
            File.Delete (file)
        

module ClusterInfo =
    open StoragePath

    type SimpleClusterInfo =
        { Name : string; Path : string; }
    let getClosedClusters () =
        Directory.EnumerateFiles(!storagePath |> ensureAndReturnDir, "*" + extension)
        |> Seq.map (fun f ->
            { Name = Path.GetFileNameWithoutExtension f; Path = f })
        
    let getOpenedClusters () =
        Directory.EnumerateDirectories(!tempStoragePath |> ensureAndReturnDir)
        |> Seq.map (fun f ->
            { Name = Path.GetFileName f; Path = f })
    type ClusterInfo =
        { Name : string; SecretAvailable : bool; IsInitialized : bool option }
    let getClusters () =
        let gc = GlobalConfig.readConfig()
        getClosedClusters()
        |> Seq.map (fun cluster ->
            let secret = GlobalConfig.readSecret cluster.Name gc
            let isInitialized, validSecret =
                match secret with
                | Some secret when not (System.String.IsNullOrEmpty secret) ->
                    try
                        Storage.openCluster cluster.Name secret
                        let c = ClusterConfig.readClusterConfig cluster.Name
                        let isInitialized = ClusterConfig.getIsInitialized c
                        Storage.closeCluster cluster.Name secret
                        Some isInitialized, true
                    with e ->
                        if Env.isVerbose then
                            eprintfn "Could not open cluster (%s): %O" cluster.Name e
                        None, false
                | _ -> None, false
            { Name = cluster.Name; SecretAvailable = validSecret; IsInitialized = isInitialized })