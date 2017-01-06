namespace ClusterManagement

open Argu

type ClusterInitArgs =
    | [<AltCommandLine("-f")>] Force
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Force -> "Initializes a new cluster, even if it is already initialized."
            
type ClusterCreateNewArgs =
    | [<AltCommandLine("-f")>] Force
    | [<AltCommandLine("-p")>] Secret of string
    | MasterAsWorker
    | [<AltCommandLine("-m")>] MasterNodes of int
    | [<AltCommandLine("-w")>] WorkerNodes of int
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Force _ -> "Do the action, even when the cluster already exists. USE THIS FLAG CAREFULLY as it will make some ressources INACCESSIBLE!"
            | Secret _ -> "The secret to save the cluster configuration and storage with."
            | MasterAsWorker -> "If given then master machines will be used as nodes as well. Default: true"
            | MasterNodes _ -> "The number of master nodes to create. Default: 1"
            | WorkerNodes _ -> "The number of worker nodes to create. We will always initialize (MasterNodes + WorkerNodes) machines, independent of MasterAsWorker. Default: 0"

type ClusterDecryptEncryptArgs =
    | [<AltCommandLine("-p")>] Secret of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Secret _ -> "The secret to encrypt or decrypt the cluster with. If the cluster is already encrypted, the secret will be changed to the given one."

type ClusterDestroyArgs =
    | Dummy of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Dummy _ -> "Dummy command line."
type ClusterDeleteArgs =
    | Dummy of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Dummy _ -> "Dummy command line."

type ClusterDeployArgs =
    | Dummy of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Dummy _ -> "Dummy command line."

type ClusterListArgs =
    | Dummy of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Dummy _ -> "Dummy command line."

type ClusterArgs =
    | [<AltCommandLine("-c")>] [<Inherit>] [<Mandatory>] Cluster of string
    | [<CliPrefix(CliPrefix.None)>] CreateNew of ParseResults<ClusterCreateNewArgs>
    | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<ClusterInitArgs>
    | [<CliPrefix(CliPrefix.None)>] Destroy of ParseResults<ClusterDestroyArgs>
    | [<CliPrefix(CliPrefix.None)>] Delete of ParseResults<ClusterDeleteArgs>
    | [<CliPrefix(CliPrefix.None)>] Deploy of ParseResults<ClusterDeployArgs>
    | [<CliPrefix(CliPrefix.None)>] Decrypt of ParseResults<ClusterDecryptEncryptArgs>
    | [<CliPrefix(CliPrefix.None)>] Encrypt of ParseResults<ClusterDecryptEncryptArgs>
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Cluster _ -> "The name of the cluster for the current operation."
            | CreateNew _ -> "Create a new cluster (only the config, without initializing)."
            | Init _ -> "Setup and provision machines for the cluster."
            | Destroy _ -> "Delete ressources associated with the cluster (volumes and machines)."
            | Delete _ -> "Delete the cluster association and config locally (destroy the cluster first, otherwise you need to cleanup manually)."
            | Deploy _ -> "Deploy an application to the cluster."
            | Decrypt _ -> "Decrypt an existing cluster (store the secret locally for future operations)."
            | Encrypt _ -> "Encrypt an existing cluster (change the secret or create one)."

type ListVolumeArgs =
    | [<AltCommandLine("-c")>] Cluster of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Cluster _ -> "The name of the cluster for filtering volumes. If none is given we show the volumes for all clusters we have access to."

type VolumeCreateArgs =
    | [<AltCommandLine("-c")>] Cluster of string
    | Size of int64
    | Name of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Cluster _ -> "The name of the cluster to create the volume for."
            | Size _ -> "The size of the new Volume."
            | Name _ -> "The name of the new Volume."

type VolumeDeleteArgs =
    | [<AltCommandLine("-c")>] Cluster of string
    | Name of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Cluster _ -> "The name of the cluster to delete the volume for."
            | Name _ -> "The name of the Volume to delete."
            
type VolumeCloneArgs =
    | SourceCluster of string
    | DestinationCluster of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | SourceCluster _ -> "The name of the cluster to copy the volumes from."
            | DestinationCluster _ -> "The name of the cluster to copy the volumes into."


type VolumeArgs =
    | [<CliPrefix(CliPrefix.None)>] List of ParseResults<ListVolumeArgs>
    | [<CliPrefix(CliPrefix.None)>] Create of ParseResults<VolumeCreateArgs>
    | [<CliPrefix(CliPrefix.None)>] Clone of ParseResults<VolumeCloneArgs>
    | [<CliPrefix(CliPrefix.None)>] Delete of ParseResults<VolumeDeleteArgs>
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | List _ -> "List all volumes, optionally only show volumes of a specific cluster."
            | Create _ -> "Create a new volume."
            | Clone _ -> "Clone volumes from one cluster to another."
            | Delete _ -> "Delete a volume."

type ConfigSetArgs =
    | Key of string
    | Value of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Key _ -> "The key of the config to create."
            | Value _ -> "The value of the config to create."
            
type ConfigGetArgs =
    | Key of string
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Key _ -> "The key of the config to get."

type ConfigArgs =
    | [<AltCommandLine("-c")>] [<Inherit>] Cluster of string
    | [<CliPrefix(CliPrefix.None)>] Set of ParseResults<ConfigSetArgs>
    | [<CliPrefix(CliPrefix.None)>] Get of ParseResults<ConfigGetArgs>
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Cluster _ -> "Change configuration of an existing cluster."
            | Set _ -> "Set a new configuration key."
            | Get _ -> "Get a configuration value by key."
            
type DockerMachineArgs =
    | [<AltCommandLine("-c")>] Cluster of string
    | [<AltCommandLine("--")>] Rest of string list
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Cluster _ -> "The cluster to run the docker-machine command on."
            | Rest _ -> "The arguments for docker-machine"

type ListClusterArgs =
    | [<AltCommandLine("--")>] Rest of string list
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Rest _ -> "Dummy for future options"

type ListArgs =
    | [<CliPrefix(CliPrefix.None)>] Cluster of ParseResults<ClusterArgs>
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Cluster _ -> "The cluster to run the docker-machine command on."



type ProvisionArgs =
    | [<Mandatory>] NodeName of string
    | [<Mandatory>] Cluster of string
    | [<Mandatory>] NodeType of NodeType
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | NodeName _ -> "The name of the node to provision."
            | Cluster _ -> "The name of the current cluster."
            | NodeType _ -> "The type of the node. Default: Worker"

type DeployArgs =
    | [<Mandatory>] Cluster of string
    | [<Mandatory>] Script of string
    | [<AltCommandLine("--")>] Rest of string list
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Script  _ -> "The deployment script for the software."
            | Cluster _ -> "The name of the current cluster."
            | Rest _ -> "The arguments for the script."

type MyArgs =
    | Version
    | [<AltCommandLine("-v")>] Verbose
    | [<CliPrefix(CliPrefix.None)>] Cluster of ParseResults<ClusterArgs>
    | [<CliPrefix(CliPrefix.None)>] List of ParseResults<ListArgs>
    | [<CliPrefix(CliPrefix.None)>] Volume of ParseResults<VolumeArgs>
    | [<AltCommandLine("docker-machine")>] [<CliPrefix(CliPrefix.None)>] DockerMachine of ParseResults<DockerMachineArgs>
    | [<CliPrefix(CliPrefix.None)>] Config of ParseResults<ConfigArgs>
    | [<CliPrefix(CliPrefix.None)>] Provision of ParseResults<ProvisionArgs>
    | [<CliPrefix(CliPrefix.None)>] Deploy of ParseResults<DeployArgs>
  with
    interface IArgParserTemplate with
        member this.Usage = 
            match this with
            | Version -> "Prints the version of the program."
            | Verbose -> "Makes the current operation more verbose."
            | Cluster _ -> "Operate on a cluster."
            | List _ -> "Lists clusters."
            | Volume _ -> "Operate on volumes."
            | Config _ -> "Operate on cluster configuration."
            | DockerMachine _ -> "Run docker-machine on a given cluster."
            | Provision _ -> "Provision machines on the cluster. You normally don't need to execute these commands manually."
            | Deploy _ -> "Deploy Software into a cluster with the given deployment script."
