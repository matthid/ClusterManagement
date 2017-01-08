namespace ClusterManagement

open System
open System.Net
open Suave
open Suave.Operators
module ServeConfig =


    type ConsulGetJson = FSharp.Data.JsonProvider<"consul-get-sample.json">
    let app =
        WebPart.choose [
            Filters.POST 
            >=> Suave.Filters.path "/v1/tokenize-config"
            >=> Suave.Writers.setMimeType "application/text; charset=utf-8"
            >=> (fun ctx -> 
                    async {
                        let file = ctx.request.files.Head
                        let! loadAsync = 
                            ConsulGetJson.AsyncLoad ("http://127.0.0.1:8500/v1/kv/yaaf/config/tokens?recurse=true")
                        let tokens =
                            loadAsync
                            |> Seq.map (fun v ->
                                { ClusterConfig.Token.Name = v.Key; ClusterConfig.Token.Value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v.Value)) })
                        Config.replaceTokensInFile tokens file.tempFilePath
                        return! Files.sendFile file.tempFilePath false ctx
                    })
            Filters.GET 
            >=> Suave.Filters.path "/v1/cluster-name"
            >=> Suave.Writers.setMimeType "application/text; charset=utf-8"
            >=> (fun ctx -> 
                    async {
                        let! loadAsync = 
                            ConsulGetJson.AsyncLoad ("http://127.0.0.1:8500/v1/kv/yaaf/config/tokens/CLUSTER_NAME")
                        
                        let tokens =
                            loadAsync
                            |> Seq.map (fun v ->
                                { ClusterConfig.Token.Name = v.Key; ClusterConfig.Token.Value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v.Value)) })
                            |> Seq.tryHead
                        match tokens with
                        | Some clusterName ->
                            return! Successful.OK clusterName.Value ctx
                        | None ->
                            return! Suave.ServerErrors.SERVICE_UNAVAILABLE "the cluster name is not available" ctx
                    })
        ]
    let startServer () =
        startWebServer defaultConfig app