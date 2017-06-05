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
    | [<AltCommandLine("-f")>] Force
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Force _ -> "Force the deletion of the docker-machines, even when we encounter errors while cleaning up."
type ClusterDeleteArgs =
    | [<AltCommandLine("-f")>] Force
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Force _ -> "Force delete the cluster locally, even when it already has been initialized."

type ClusterListArgs =
    | Dummy of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Dummy _ -> "Dummy command line."

type ClusterArgs =
    | [<AltCommandLine("-c")>] [<Inherit>] [<Mandatory>] Cluster of string
    | [<CliPrefix(CliPrefix.None)>] Encrypt of ParseResults<ClusterDecryptEncryptArgs>
    | [<CliPrefix(CliPrefix.None)>] Decrypt of ParseResults<ClusterDecryptEncryptArgs>
    | [<CliPrefix(CliPrefix.None)>] CreateNew of ParseResults<ClusterCreateNewArgs>
    | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<ClusterInitArgs>
    | [<CliPrefix(CliPrefix.None)>] Destroy of ParseResults<ClusterDestroyArgs>
    | [<CliPrefix(CliPrefix.None)>] Delete of ParseResults<ClusterDeleteArgs>
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The name of the cluster for the current operation."
            | CreateNew _ -> "Create a new cluster (only the config, without initializing)."
            | Init _ -> "Setup and provision machines for the cluster."
            | Destroy _ -> "Delete ressources associated with the cluster (volumes and machines)."
            | Delete _ -> "Delete the cluster association and config locally (destroy the cluster first, otherwise you need to cleanup manually)."
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
    | [<Mandatory>] [<AltCommandLine("-c")>] Cluster of string
    | Size of int64
    | [<Mandatory>] [<AltCommandLine("-n")>] Name of string
    | [<AltCommandLine("-g")>] Global
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The name of the cluster to create the volume for."
            | Size _ -> "The size of the new Volume in bytes. Defaults to 1G (1024 * 1024 * 1024)"
            | Global -> "Global -> Volume is not associated with any cluster and will not be deleted with the cluster."
            | Name _ -> "The name of the new Volume."

type VolumeDeleteArgs =
    | [<AltCommandLine("-c")>] Cluster of string
    | [<Mandatory>] [<AltCommandLine("-n")>] Name of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The name of the cluster to delete the volume for."
            | Name _ -> "The dataset-id of the Volume to delete."

type VolumeCloneArgs =
    | SourceCluster of string
    | DestinationCluster of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | SourceCluster _ -> "The name of the cluster to copy the volumes from."
            | DestinationCluster _ -> "The name of the cluster to copy the volumes into."

type VolumeCopyContentsArgs =
    | [<AltCommandLine("-c")>] Cluster of string
    | [<AltCommandLine("-n")>] Volume of string
    | [<AltCommandLine("-l")>] LocalFolder of string
    | [<AltCommandLine("-f")>] FileName of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The name of the cluster to copy the volumes from or to."
            | Volume _ -> "The name of the volume to interact with."
            | LocalFolder _ -> "The name of the local folder to interact with."
            | FileName _ -> "Set to interact with a single file (relative to the directories) only."

type VolumeArgs =
    | [<CliPrefix(CliPrefix.None)>] List of ParseResults<ListVolumeArgs>
    | [<CliPrefix(CliPrefix.None)>] Create of ParseResults<VolumeCreateArgs>
    | [<CliPrefix(CliPrefix.None)>] Download of ParseResults<VolumeCopyContentsArgs>
    | [<CliPrefix(CliPrefix.None)>] Upload of ParseResults<VolumeCopyContentsArgs>
    | [<CliPrefix(CliPrefix.None)>] Clone of ParseResults<VolumeCloneArgs>
    | [<CliPrefix(CliPrefix.None)>] Delete of ParseResults<VolumeDeleteArgs>
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | List _ -> "List all volumes, optionally only show volumes of a specific cluster."
            | Create _ -> "Create a new volume."
            | Download _ -> "Download a given volume to a folder."
            | Upload _ -> "Upload a given folder to a volume."
            | Clone _ -> "Clone volumes from one cluster to another."
            | Delete _ -> "Delete a volume."

type ConfigSetArgs =
    | [<AltCommandLine("-c")>] [<Mandatory>] Cluster of string
    | Key of string
    | Value of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "Change configuration of an existing cluster."
            | Key _ -> "The key of the config to create."
            | Value _ -> "The value of the config to create."

type ConfigUploadArgs =
    | [<AltCommandLine("-c")>] [<Mandatory>] Cluster of string
    | Name of string
    | [<AltCommandLine("-f")>] FilePath of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "Upload a configuration file for an existing cluster."
            | Name _ -> "The name of the config to create (can contain / for folders)."
            | FilePath _ -> "The file to upload."

type ConfigDownloadArgs =
    | [<AltCommandLine("-c")>] [<Mandatory>] Cluster of string
    | Name of string
    | [<AltCommandLine("-f")>] FilePath of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "Download a configuration file of an existing cluster."
            | Name _ -> "The name of the config to retrieve (can contain / for folders)."
            | FilePath _ -> "The file to write."

type ConfigGetArgs =
    | [<AltCommandLine("-c")>] [<Mandatory>] Cluster of string
    | Key of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "Change configuration of an existing cluster."
            | Key _ -> "The key of the config to get."

type ListConfigArgs =
    | [<AltCommandLine("-c")>] Cluster of string
    | [<AltCommandLine("-v")>] IncludeValues
    | [<AltCommandLine("-f")>] IncludeFiles
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The name of the cluster for filtering config values. If none is given we show the configurations for all clusters we have access to."
            | IncludeValues _ -> "Print values as well."
            | IncludeFiles _ -> "Print available files as well."

type ConfigCopyArgs =
    | [<Mandatory>] [<AltCommandLine("-s")>] Source of string
    | [<Mandatory>] [<AltCommandLine("-d")>] Dest of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Source _ -> "The name of the source cluster to copy configurations from."
            | Dest _ -> "The name of the destination cluster to copy the configuration into."

type ConfigUpdateClusterArgs =
    | [<AltCommandLine("-c")>] Cluster of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The cluster to update."

type ConfigArgs =
    | [<CliPrefix(CliPrefix.None)>] Upload of ParseResults<ConfigUploadArgs>
    | [<CliPrefix(CliPrefix.None)>] Download of ParseResults<ConfigDownloadArgs>
    | [<CliPrefix(CliPrefix.None)>] Set of ParseResults<ConfigSetArgs>
    | [<CliPrefix(CliPrefix.None)>] Get of ParseResults<ConfigGetArgs>
    | [<CliPrefix(CliPrefix.None)>] Copy of ParseResults<ConfigCopyArgs>
    | [<CliPrefix(CliPrefix.None)>] List of ParseResults<ListConfigArgs>
    | [<CliPrefix(CliPrefix.None)>] UpdateCluster of ParseResults<ConfigUpdateClusterArgs>
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Upload _ -> "Upload a new configuration file."
            | Download _ -> "Download a new configuration file."
            | Set _ -> "Set a new configuration key."
            | Get _ -> "Get a configuration value by key."
            | Copy _ -> "Copy configuration from one cluster to another."
            | List _ -> "List all configs, optionally only show configs of a specific cluster."
            | UpdateCluster _ -> "Updates the cluster instance with the current values."

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
    | [<CliPrefix(CliPrefix.None)>] Cluster of ParseResults<ListClusterArgs>
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The cluster to run the docker-machine command on."



type ProvisionArgs =
    | [<Mandatory>] NodeName of string
    | [<AltCommandLine("-c")>] [<Mandatory>] Cluster of string
    | [<Mandatory>] NodeType of NodeType
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | NodeName _ -> "The name of the node to provision."
            | Cluster _ -> "The name of the current cluster."
            | NodeType _ -> "The type of the node. Default: Worker"

type RunArgs =
    | [<AltCommandLine("-c")>] [<Mandatory>] Cluster of string
    | [<Mandatory>] Script of string
    | [<AltCommandLine("--")>] Rest of string list
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Script  _ -> "The script to execute."
            | Cluster _ -> "The name of the current cluster."
            | Rest _ -> "The arguments for the script."

type ServiceArgs =
    | [<CliPrefix(CliPrefix.None)>] Add of ParseResults<ClusterArgs>
    | [<CliPrefix(CliPrefix.None)>] Backup of ParseResults<ClusterArgs>
    | [<CliPrefix(CliPrefix.None)>] Restore of ParseResults<ClusterArgs>
    | [<AltCommandLine("-c")>] [<Mandatory>] Cluster of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Add  _ -> "Add a service to the cluster."
            | Backup _ -> "backup an existing service."
            | Restore _ -> "Restore a service from the given backup."
            | Cluster _ -> "the cluster to operate on"

type ExportArgs =
    | [<AltCommandLine("-c")>] [<Mandatory>] Cluster of string
    | IncludeVolumeContents
    | IncludeVolumeConfiguration
    | TargetFile of string
    | [<AltCommandLine("-p")>] Secret
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The name of the current cluster."
            | IncludeVolumeContents  _ -> "Should the export contain the volume data?"
            | IncludeVolumeConfiguration  _ -> "Should the export contain the volume configuration?"
            | TargetFile  _ -> "The target file to export the data into. Note that the file may become huge when all volume data is exported!"
            | Secret _ -> "The file will be encrypted with the given secret."

type ImportArgs =
    | ImportFile of string
    | Secret of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | ImportFile _ -> "The export file to import."
            | Secret  _ -> "The secret of the cluster."

type ServeConfigArgs =
    | ConsulAddress of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | ConsulAddress _ -> "The consul server to connect to."

type DeployConfigArgs =
    | [<AltCommandLine("-c")>] Dummy of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Dummy _ -> "The name of the cluster for filtering config values. If none is given we show the configurations for all clusters we have access to."

type OpenClusterArgs =
    | [<AltCommandLine("-c")>] Cluster of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The name of the cluster we want to open."

type CloseClusterArgs =
    | [<AltCommandLine("-c")>] Cluster of string
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Cluster _ -> "The name of the cluster we want to close."

type InternalArgs =
    | [<CliPrefix(CliPrefix.None)>] ServeConfig of ParseResults<ServeConfigArgs>
    | [<CliPrefix(CliPrefix.None)>] DeployConfig of ParseResults<DeployConfigArgs>
    | [<CliPrefix(CliPrefix.None)>] OpenCluster of ParseResults<OpenClusterArgs>
    | [<CliPrefix(CliPrefix.None)>] CloseCluster of ParseResults<CloseClusterArgs>
  with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | ServeConfig _ -> "INTERNAL: Start a simple webserver which deploys a list of config-file tokens, which can be easily consumed by a bash script."
            | DeployConfig _ -> "INTERNAL: Deploy the configuration files to the cluster"
            | OpenCluster _ -> "INTERNAL: Decrypt a cluster file and leave it in the temp folder (only for debugging)"
            | CloseCluster _ -> "INTERNAL: Encrypt a cluster folder and delete the temp folder (only for debugging)"

type MyArgs =
    | Version
    | [<AltCommandLine("-v")>] Verbose
    | [<CliPrefix(CliPrefix.None)>] Cluster of ParseResults<ClusterArgs>
    | [<CliPrefix(CliPrefix.None)>] List of ParseResults<ListArgs>
    | [<CliPrefix(CliPrefix.None)>] Volume of ParseResults<VolumeArgs>
    | [<AltCommandLine("docker-machine")>] [<CliPrefix(CliPrefix.None)>] DockerMachine of ParseResults<DockerMachineArgs>
    | [<CliPrefix(CliPrefix.None)>] Config of ParseResults<ConfigArgs>
    | [<CliPrefix(CliPrefix.None)>] Provision of ParseResults<ProvisionArgs>
    | [<CliPrefix(CliPrefix.None)>] Run of ParseResults<RunArgs>
    | [<CliPrefix(CliPrefix.None)>] Service of ParseResults<ServiceArgs>
    | [<CliPrefix(CliPrefix.None)>] Export of ParseResults<ExportArgs>
    | [<CliPrefix(CliPrefix.None)>] Import of ParseResults<ImportArgs>
    | [<CliPrefix(CliPrefix.None)>] Internal of ParseResults<InternalArgs>
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
            | Run _ -> "Run a given deployment/backup/restore script on the cluster."
            | Service _ -> "Deploy Software into a cluster with the given deployment script."
            | Export _ -> "Export the current cluster."
            | Import _ -> "Import a given exported cluster."
            | Internal _ -> "INTERNAL: some internal commands used by clustermanagement itself."
