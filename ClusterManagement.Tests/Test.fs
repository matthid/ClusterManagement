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
        
