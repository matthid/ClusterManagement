namespace ClusterManagement.Tests

open NUnit.Framework
open ClusterManagement
open System.IO
open Swensen.Unquote

type SimulatedProcess =
    { AssertProcess : RawCreateProcess -> unit; Output : ProcessOutput; ExitCode : int }
    static member Simple (cmdLine : string, output) =
        let assertCmdLine (c:RawCreateProcess) =
            Assert.AreEqual (cmdLine, c.CommandLine)
        { AssertProcess = assertCmdLine; Output = { Output = output; Error = "" }; ExitCode = 0 }

module TestHelper =
    let setProcessAssertion processes =
        let arr = processes |> List.toArray
        let mutable cur = 0
        RawProc.processStarter <-
            { new IProcessStarter with
                member x.Start c =
                    if cur >= arr.Length then failwithf "Tried to start an unconfigured process \n%s \n\n %A" c.CommandLine c
                    let currentAssertion : SimulatedProcess = arr.[cur]
                    cur <- cur + 1

                    currentAssertion.AssertProcess c

                    let output =
                        match c.OutputRedirected with
                        | true -> Some currentAssertion.Output
                        | false -> None

                    async {
                        return currentAssertion.ExitCode, output
                    }
            }

    let changeTmpDir () =
        let dir = Path.GetTempFileName()
        File.Delete dir
        Directory.CreateDirectory dir |> ignore
        let oldDir = System.Environment.CurrentDirectory
        System.Environment.CurrentDirectory <- dir
        { new System.IDisposable with
            member x.Dispose() =
                System.Environment.CurrentDirectory <- oldDir
                Directory.Delete(dir, true) }

    let setupCluster cluster =

        ()

[<TestFixture>]
type Test() = 
    [<Test>]
    member __.``Test Yaml Config load`` () =
        let c = Path.GetTempFileName()
        File.WriteAllText(c, @"secrets:
  - clustername: test
    secret: secret
  - clustername: test2
    secret: secret
"            )

        let cc = GlobalConfig.ClusterManagementConfig()
        //cc.secrets.Clear()
        if Env.isVerbose then
            printfn "loading global config file '%s'" c
        if File.Exists c then
            cc.Load c
        
        File.Delete c
        ()
        
    [<Test>]
    member __.``Test Volume Create when cluster was not initialized`` () =
        use dir = TestHelper.changeTmpDir()
        [ ] |> TestHelper.setProcessAssertion
        ClusterConfig.setInitialConfig "cluster" false
        let res = Assert.Throws<exn>(fun _ ->
            Volume.create false "cluster" "volume" 1024L
                |> Async.StartImmediateAsTask
                |> fun t -> t.GetAwaiter().GetResult()
                |> ignore)
        Assert.IsTrue(res.Message.Contains "Cannot run process for cluster 'cluster' when it is not initialized!")

    [<Test>]
    member __.``Test Volume Create when no matching one exists`` () =
        use dir = TestHelper.changeTmpDir()
        let lsout = """
DRIVER              VOLUME NAME
flocker             backup_yaaf-prod-ldap
flocker             yaaf-prod-clustermanagement
rexray/ebs:latest   yaaf-prod_unmounted
rexray/ebs:latest   yaaf-teamspeak_clustermanagement
rexray/ebs:latest   yaaf-teamspeak_teamspeak"""

        [ SimulatedProcess.Simple("""docker-machine "ssh" "cluster-master-01" "'sudo' 'docker' 'volume' 'ls'" """.TrimEnd(), lsout)
          // Note: Size needs to be roundet to 1 (which is the minimum acceptable value)
          SimulatedProcess.Simple("""docker-machine "ssh" "cluster-master-01" "'sudo' 'docker' 'volume' 'create' '--name=cluster_volume' '--driver=rexray/ebs' '--opt=size=1'" """.TrimEnd(), "")
        ]
            |> TestHelper.setProcessAssertion
        ClusterConfig.setInitialConfig "cluster" false
        ClusterConfig.setClusterInitialized "cluster" true
        let result =
            Volume.create false "cluster" "volume" 1024L
                |> Async.RunSynchronously

        let ci = Some { Volume.ClusterDockerInfo.SimpleName = "volume"; Volume.ClusterDockerInfo.Cluster = "cluster" }
        let expected =
            { Volume.ClusterDockerVolume.Info = { Name = "cluster_volume"; Driver = "rexray/ebs" }
              Volume.ClusterDockerVolume.ClusterInfo = ci }
        Assert.AreEqual(expected, result)