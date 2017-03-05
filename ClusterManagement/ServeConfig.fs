namespace ClusterManagement

open System
open System.Net.Http
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
                            ConsulGetJson.AsyncLoad ("http://consul:8500/v1/kv/yaaf/config/tokens?recurse=true")
                        let tokens =
                            loadAsync
                            |> Seq.map (fun v ->
                                { ClusterConfig.Token.Name = v.Key; ClusterConfig.Token.Value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v.Value)) })
                        Config.replaceTokensInFile tokens file.tempFilePath
                        return! Files.sendFile file.tempFilePath false ctx
                    })
            Filters.PUT 
            >=> Suave.Filters.pathStarts "/v1/config-files/"
            >=> Suave.Writers.setMimeType "application/text; charset=utf-8"
            >=> (fun ctx -> 
                    async {
                        let file = ctx.request.files.Head
                        let filePath = ctx.request.url.PathAndQuery.Substring("/v1/config-files/".Length)
                        let bytes = System.IO.File.ReadAllBytes file.tempFilePath
                        let data = Convert.ToBase64String(bytes)
                        let request =
                            sprintf """[ { "KV": { "Verb": "set", "Key": "/yaaf/config/files/%s", "Value": "%s" } } ]""" 
                                filePath data
                        use client = new HttpClient()
                        let! result = client.PutAsync("http://consul:8500/v1/txn", new StringContent(request)) |> Async.AwaitTask
                        let! res = result.Content.ReadAsStringAsync()
                        if not result.IsSuccessStatusCode then
                            eprintfn "Consul responded: %s" res
                        result.EnsureSuccessStatusCode() |> ignore
                        return! Successful.OK "life is good." ctx
                    })
            Filters.GET 
            >=> Suave.Filters.pathStarts "/v1/config-files/"
            >=> Suave.Writers.setMimeType "application/text; charset=utf-8"
            >=> (fun ctx -> 
                    async {
                        let filePath = ctx.request.url.PathAndQuery.Substring("/v1/config-files/".Length)
                        
                        let! loadAsync = 
                            ConsulGetJson.AsyncLoad (sprintf "http://consul:8500/v1/kv/yaaf/config/files/%s" filePath)
                        
                        let tokens =
                            loadAsync
                            |> Seq.map (fun v -> Convert.FromBase64String(v.Value))
                            |> Seq.tryHead
                        match tokens with
                        | Some bytes ->
                            let tmpFile = System.IO.Path.GetTempFileName()
                            System.IO.File.WriteAllBytes(tmpFile, bytes)
                            return! Files.sendFile filePath true ctx
                        | None ->
                            return! Suave.RequestErrors.NOT_FOUND "the given file is not available" ctx
                    })
            Filters.GET 
            >=> Suave.Filters.path "/v1/cluster-name"
            >=> Suave.Writers.setMimeType "application/text; charset=utf-8"
            >=> (fun ctx -> 
                    async {
                        let! loadAsync = 
                            ConsulGetJson.AsyncLoad ("http://consul:8500/v1/kv/yaaf/config/tokens/CLUSTER_NAME")
                        
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
    let config =
        { defaultConfig with bindings = [ HttpBinding.create HTTP IPAddress.Any 80us ] }
    let startServer () =
        startWebServer config app