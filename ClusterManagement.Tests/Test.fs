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
    member __.``Test parse flockerctl long list output`` () =
        let out = """DATASET                                SIZE     METADATA                                         STATUS         SERVER
f71ead1e-f9c7-440f-9e58-17aca3545eb7   75.00G   name=49940210b4895365321f5aa1f36df3638d1f8b511   attached ✅   d266bf3a (127.0.0.1)
                                                1a0445282cf4c4f393b3238
0abf74c3-fe18-4893-b5bc-3cb268b7b123   75.00G   name=d403295e74a16b42e120b29ba68cd8090f54d661c   attached ✅   d266bf3a (127.0.0.1)
                                                5cee47f9e59400bb210ef1c
6bd63a43-85f6-4f4a-a200-299eaaa705a3   75.00G   name=98a75d00ad02d6035937bfa659ec420faafe76524   attached ✅   d266bf3a (127.0.0.1)
                                                5510634d2ec9dca594dd63f
8a0cf8f4-4585-4d40-b252-efc5dd41123d   75.00G   name=4b5cde0c3c6b61671b4b74696a7c4f7799c2587d8   attached ✅   d266bf3a (127.0.0.1)
                                                fa47b76f207e6d84951374f
039bb71f-b023-4762-9aa5-c5f94c90b87d   75.00G   name=024fecd9acf55f096e217af2eb3b0603f515081a9   attached ✅   d266bf3a (127.0.0.1)
                                                526944896bdd19a45732e67
b744ce5d-f76a-43ae-a64a-ee145d80e8b1   75.00G   name=0a5d0c08b0ad5d69c6280934445c47212a8ae800e   attached ✅   d266bf3a (127.0.0.1)
                                                4af781c2d3e5a0a5a7be613
9a88295a-dbc0-4543-a202-5ff2eda6563e   1.00G    cluster=yaaf-prod,name=yaaf-prod-consul-         attached ✅   d266bf3a (127.0.0.1)
                                                master-03
e467717b-a6a4-4eab-a784-9c1267f477d8   0.10G    cluster=yaaf-prod,name=yaaf-prod-ldap            attached ✅   d266bf3a (127.0.0.1)
54773e77-41d2-4c30-b54a-829db6438f6f   75.00G   name=39e58521409c2e749c390647e290c53f28c02d9f5   attached ✅   d266bf3a (127.0.0.1)
                                                b79b60fa2b372afb907ff6f
1a30df01-3526-4ee4-b2a3-e175e2b9cdf6   75.00G   name=2f0da52745107d46128a0a00ae0e267fec3c156f9   attached ✅   d266bf3a (127.0.0.1)
                                                0db1059a9a7621cf1d0b248
ad03ff2c-1ac9-4d3d-8f9c-93b8f1cc8046   1.00G    cluster=yaaf-prod,name=yaaf-prod-consul-         attached ✅   d266bf3a (127.0.0.1)
                                                master-02
4e2c84f4-15b1-423f-920e-bba6c6b4dec6   1.00G    cluster=yaaf-prod,name=yaaf-prod-consul-         attached ✅   d266bf3a (127.0.0.1)
                                                master-01
b9237dcb-bba1-49d3-903f-3d5c0fe1d4dc   75.00G   name=backup_yaaf-prod-ldap                       attached ✅   d266bf3a (127.0.0.1)
b0bd7494-43eb-4ad6-b1ee-30a2e4779d14   75.00G   name=ad4edf9f0b426c164b82abfc2723d942bbd61c930   attached ✅   d266bf3a (127.0.0.1)
                                                67f643632d1e74e90b95d34
60c179cf-0070-4d2d-bfe0-a75526057471   75.00G   name=0aec3dbc3a19a6143d75e13c0eae5c19066a671c0   attached ✅   d266bf3a (127.0.0.1)
                                                7d10e55e9ac4f8af9b4455d
9002f3ea-1565-49c0-aaab-83bbe978bdbd   75.00G   name=5e624eaedf72e75b5e3d9ab2c17bd0428cab16771   attached ✅   d266bf3a (127.0.0.1)
                                                a821e79fa587264b8e02467

"""

        let res = Volume.parseList out
        
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


