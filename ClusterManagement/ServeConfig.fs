namespace ClusterManagement

open System
open System.Net.Http
open System.Net
open Suave
open Suave.Operators
module ServeConfig =

    module TempDirs =
        open System.IO
        // As we run in docker we don't care about crashes -> files will be deleted
        let private cleanupDirs = new System.Collections.Generic.List<_>()
        let private cleanup() =
            let now = DateTime.UtcNow
            for (dir, date) as t in cleanupDirs |> Seq.toList do
                if date + TimeSpan.FromMinutes 10.0 < now then
                    Directory.Delete(dir, true)
                    cleanupDirs.Remove t |> ignore
                    
        let writeTempFile fileName bytes =
            cleanup()
            let tmpDir = Path.GetTempFileName()
            File.Delete (tmpDir)
            Directory.CreateDirectory (tmpDir) |> ignore
            cleanupDirs.Add(tmpDir, DateTime.UtcNow)
            let tmpFile = Path.Combine(tmpDir, fileName)
            File.WriteAllBytes(tmpFile, bytes)
            tmpFile

    type ConsulGetJson = FSharp.Data.JsonProvider<"consul-get-sample.json">
    let private tokenStart = "yaaf/config/tokens/"
    let private tokenFromProvider logger (p:ConsulGetJson.Root[]) =
        p
        |> Seq.map (fun v ->
            let key = if (v.Key.StartsWith(tokenStart)) then v.Key.Substring(tokenStart.Length) else v.Key
            if key = v.Key then
                logger Logging.LogLevel.Error (fun _ -> sprintf "Token '%s' not starting with '%s'" v.Key tokenStart)

            { ClusterConfig.Token.Name = key; ClusterConfig.Token.Value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v.Value)) })
        |> Seq.toList
    let private createLogger ctx =
        (fun level createMsg -> ctx.runtime.logger.log level (fun level -> Logging.Message.event level (createMsg(level))) |> Async.RunSynchronously)
    let app =
        WebPart.choose [
            Filters.POST 
            >=> Suave.Filters.path "/v1/tokenize-config"
            >=> Suave.Writers.setMimeType "application/text; charset=utf-8"
            >=> (fun ctx -> 
                    async {
                        let file = ctx.request.files.Head
                        // curl http://consul:8500/v1/kv/yaaf/config/tokens?recurse=true
                        let! loadAsync = 
                            ConsulGetJson.AsyncLoad ("http://consul:8500/v1/kv/yaaf/config/tokens?recurse=true")
                        let tokens =
                            loadAsync |> tokenFromProvider (createLogger ctx)
                        Config.replaceTokensInFile tokens file.tempFilePath
                        return! Files.sendFile file.tempFilePath false ctx
                    })
            Filters.PUT 
            >=> Suave.Filters.pathStarts "/v1/config-files/"
            >=> Suave.Writers.setMimeType "application/text; charset=utf-8"
            >=> (fun ctx -> 
                    async {
                        match ctx.request.files with
                        | [ file ] ->
                            
                            let filePath = ctx.request.url.PathAndQuery.Substring("/v1/config-files/".Length)
                            let bytes = System.IO.File.ReadAllBytes file.tempFilePath
                            let data = Convert.ToBase64String(bytes)
                            let request =
                                sprintf """[ { "KV": { "Verb": "set", "Key": "yaaf/config/files/%s", "Value": "%s" } } ]""" 
                                    filePath data
                            let client = new HttpClient()
                            try
                                // curl -H 'Content-Type: application/json' -X PUT -d '[ {"KV": { "Verb": "set", "Key": "yaaf/config/files/ssl/cacert.pem", "Value": "dGVzdA==" } } ]' http://consul:8500/v1/txn
                                let! result = client.PutAsync("http://consul:8500/v1/txn", new StringContent(request)) |> Async.AwaitTask
                                let! res = result.Content.ReadAsStringAsync()
                                if not result.IsSuccessStatusCode then
                                    eprintfn "Consul responded: %s" res
                                result.EnsureSuccessStatusCode() |> ignore
                                return! Successful.OK "life is good." ctx
                            finally
                                try
                                    client.Dispose()
                                with e ->
                                    eprintfn "Error while disposing 'client': %O" e
                        | _ ->
                            return! RequestErrors.BAD_REQUEST (sprintf "expected exactly one file, but got %d." ctx.request.files.Length) ctx
                            
                    })
            Filters.GET // test with: curl http://clustermanagement/v1/config-files/ssl/cacert.pem
            >=> Suave.Filters.pathStarts "/v1/config-files/"
            >=> Suave.Writers.setMimeType "application/text; charset=utf-8"
            >=> (fun ctx -> 
                    async {
                        let filePath = ctx.request.url.PathAndQuery.Substring("/v1/config-files/".Length)
                        let fileName = System.IO.Path.GetFileName filePath
                        let! loadAsync = 
                            ConsulGetJson.AsyncLoad (sprintf "http://consul:8500/v1/kv/yaaf/config/files/%s" filePath)
                        
                        let tokens =
                            loadAsync
                            |> Seq.map (fun v -> Convert.FromBase64String(v.Value))
                            |> Seq.tryHead
                        match tokens with
                        | Some bytes ->
                            let tmpFile = TempDirs.writeTempFile fileName bytes
                            return! Files.file tmpFile ctx
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
                            |> tokenFromProvider (createLogger ctx)
                            |> Seq.tryHead
                        match tokens with
                        | Some clusterName ->
                            return! Successful.OK clusterName.Value ctx
                        | None ->
                            return! Suave.ServerErrors.SERVICE_UNAVAILABLE "the cluster name is not available" ctx
                    })
        ]
    let mimeTypes =
        Writers.defaultMimeTypesMap
        @@ (function | _ -> Writers.createMimeType "application/octet-stream" false)
    let mylogging (level:Logging.LogLevel) getMsg =
      if level > Logging.LogLevel.Info then
        let (msg:Logging.Message) = getMsg level
        match msg.value with
        | Logging.PointValue.Event event -> printfn "logger: %A" msg
        | _ -> ()

    let config =
        { defaultConfig with 
            bindings = [ HttpBinding.create HTTP IPAddress.Any 80us ]
            mimeTypesMap = mimeTypes
            logger = { new Logging.Logger with
                        member __.name = [|"mylogger"|]
                        member __.log level getMsg =
                            async {
                                mylogging level getMsg
                            } 
                        member __.logWithAck level getMsg =
                            async {
                                mylogging level getMsg
                            }}
        }
    let startServer () =
        startWebServer config app