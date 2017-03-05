namespace ClusterManagement.Tests

open NUnit.Framework
open ClusterManagement
open System.IO
open Swensen.Unquote

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
    member __.``Test parse flockerctl list output`` () =
        let out = @"DATASET                                SIZE     METADATA                     STATUS         SERVER               
4bd4f27f-65dd-435b-aaa9-2bdf28f6f3d8   75.00G   name=blub-master-01-consul   attached ✅   31f83f34 (127.0.0.1) 
3592fdb6-fbe7-434c-82bc-02307f92c39c   1.00G    cluster=blub,name=test       detached       31f83f34 (127.0.0.1) "
        
        let res = Volume.parseList out
        
        test 
            <@ res = [ { Volume.FlockerVolume.Dataset = "4bd4f27f-65dd-435b-aaa9-2bdf28f6f3d8"; 
                         Volume.FlockerVolume.Size = "75.00G"; 
                         Volume.FlockerVolume.Metadata = "name=blub-master-01-consul"; 
                         Volume.FlockerVolume.Status = "attached"; 
                         Volume.FlockerVolume.ServerId = "31f83f34"
                         Volume.FlockerVolume.ServerIP = "(127.0.0.1)"}
                       { Volume.FlockerVolume.Dataset = "3592fdb6-fbe7-434c-82bc-02307f92c39c"; 
                         Volume.FlockerVolume.Size = "1.00G"; 
                         Volume.FlockerVolume.Metadata = "cluster=blub,name=test"; 
                         Volume.FlockerVolume.Status = "detached"; 
                         Volume.FlockerVolume.ServerId = "31f83f34"
                         Volume.FlockerVolume.ServerIP = "(127.0.0.1)"}] @>

        ()
          
    [<Test>]
    member __.``Test parse flockerctl list output (example-01)`` () =
        let assembly = System.Reflection.Assembly.GetExecutingAssembly()
        let resourceName = "flockerctl-list-example-01.txt";

        let out =
           use stream = assembly.GetManifestResourceStream(resourceName)
           use reader = new StreamReader(stream)
           reader.ReadToEnd()

        let res = Volume.parseList out
        
        test 
            <@ res = [ { Volume.FlockerVolume.Dataset = "5315f55b-f553-4351-ba7c-b2b787650e61"; 
                         Volume.FlockerVolume.Size = "75.00G"; 
                         Volume.FlockerVolume.Metadata = "name=master-01-consul"; 
                         Volume.FlockerVolume.Status = "attached"; 
                         Volume.FlockerVolume.ServerId = "fced724d"
                         Volume.FlockerVolume.ServerIP = "(127.0.0.1)"}
                       { Volume.FlockerVolume.Dataset = "262a1039-e780-4a0d-89c7-e71025717357"; 
                         Volume.FlockerVolume.Size = "75.00G"; 
                         Volume.FlockerVolume.Metadata = "name=master-03-consul"; 
                         Volume.FlockerVolume.Status = "attached"; 
                         Volume.FlockerVolume.ServerId = "fced724d"
                         Volume.FlockerVolume.ServerIP = "(127.0.0.1)"}
                       { Volume.FlockerVolume.Dataset = "b2425a25-c054-4997-9021-cc557ec8deb9"; 
                         Volume.FlockerVolume.Size = "75.00G"; 
                         Volume.FlockerVolume.Metadata = "name=master-02-consul"; 
                         Volume.FlockerVolume.Status = "attached"; 
                         Volume.FlockerVolume.ServerId = "fced724d"
                         Volume.FlockerVolume.ServerIP = "(127.0.0.1)"}
                       { Volume.FlockerVolume.Dataset = "c78842a6-d5d1-4418-a8c8-0a0b19740982"; 
                         Volume.FlockerVolume.Size = "1.00G"; 
                         Volume.FlockerVolume.Metadata = "cluster=seafile-temp,name=seafile-temp-master-01-consul"; 
                         Volume.FlockerVolume.Status = "attached"; 
                         Volume.FlockerVolume.ServerId = "fced724d"
                         Volume.FlockerVolume.ServerIP = "(127.0.0.1)"}] @>

        ()
        
    [<Test>]
    member __.``Test parse flockerctl list-nodes output`` () =
        let out = @"SERVER     ADDRESS   
31f83f34   127.0.0.1 "

        let res = Volume.parseListNodes out
        
        test <@ res = [ { Volume.FlockerNode.Server = "31f83f34"; Address = "127.0.0.1" } ] @>
        
        ()
    
    [<Test>]
    member __.``Test parse ifconfig docker0 output`` () =
        let out = @"docker0   Link encap:Ethernet  HWaddr 02:42:85:82:4a:28  
          inet addr:172.17.0.1  Bcast:0.0.0.0  Mask:255.255.0.0
          inet6 addr: fe80::42:85ff:fe82:4a28/64 Scope:Link
          UP BROADCAST MULTICAST  MTU:1500  Metric:1
          RX packets:78 errors:0 dropped:0 overruns:0 frame:0
          TX packets:8 errors:0 dropped:0 overruns:0 carrier:0
          collisions:0 txqueuelen:0 
          RX bytes:5444 (5.4 KB)  TX bytes:648 (648.0 B)"
        let ip = DockerMachine.parseIfConfig out
        test <@ ip = Some "172.17.0.1" @>
        ()


