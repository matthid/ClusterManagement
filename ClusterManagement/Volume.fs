namespace ClusterManagement

module Volume =
    let flockerctl cluster node flockerCtlArgs =
      async {
        let res =
            DockerMachine.runOnNode cluster node
                (sprintf "sudo docker run --net=host --rm -e FLOCKER_CERTS_PATH=/etc/flocker -e FLOCKER_USER=flockerctl -e FLOCKER_CONTROL_SERVICE=\"%s-master-01\" -e CONTAINERIZED=1 -v /:/host -v $PWD:/pwd:z clusterhq/uft:latest flockerctl %s"
                cluster flockerCtlArgs)
        return! res
      }
    type FlockerNode =
        { Server : string; Address : string }
    let internal parseListNodes (out:string) =
        let splitLine (line:string) =
            let (s:string array) = line.Split ([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
            assert (s.Length = 2)
            { Server = s.[0]; Address = s.[1] }

        let items =
            out.Split([| '\r'; '\n' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Seq.map splitLine
            |> Seq.toList
        match items with
        | h :: t ->
             assert (h.Server = "SERVER")
             assert (h.Address = "ADDRESS")
             t
        | _ ->
            failwithf "failed to parse list-nodes: %s" out
            
    let listNodes cluster =
      async {
        let! res = flockerctl cluster "master-01" "list-nodes"
        res |> Proc.failOnExitCode |> ignore
        return parseListNodes res.Output.StdOut
      }

    type FlockerVolume =
        { Dataset : string; Size : string; Metadata : string; Status : string; ServerId : string; ServerIP : string }
    let internal parseList (out:string) =
        //DATASET                                SIZE     METADATA                     STATUS         SERVER               
        //4bd4f27f-65dd-435b-aaa9-2bdf28f6f3d8   75.00G   name=blub-master-01-consul   attached ✅   31f83f34 (127.0.0.1) 
        let splitLine (line:string) =
            let (s:string array) = line.Split ([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
            assert (s.Length > 5)
            { Dataset = s.[0]; Size = s.[1]; Metadata = s.[2]; Status = s.[3]; ServerId = s.[s.Length - 2]; ServerIP = s.[s.Length - 1]}

        let items =
            out.Split([| '\r'; '\n' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Seq.skip 1
            |> Seq.map splitLine
            |> Seq.toList
        items
        
    let list cluster =
      async {
        let! res = flockerctl cluster "master-01" "list"
        res |> Proc.failOnExitCode |> ignore
        return parseList res.Output.StdOut
      } 

    
    let create cluster name size =
      async {
        // flockerctl check if exists
        // docker run flockerctl volume create
        let! vols = list cluster
        
        match vols |> Seq.tryFind (fun v -> v.Metadata.Contains (sprintf "name=%s" name)) with
        | Some v ->
            eprintfn "Not creating volume '%s' as it already exists (id: %s)." name v.Dataset
        | None ->
            // optimization: we don't need to call list-nodes when we already have an id
            let maybeNodeId = vols |> Seq.tryFind (fun v -> not (isNull v.ServerId))
            let nodeId = ref Unchecked.defaultof<_>
            match maybeNodeId with
            | Some n -> nodeId := n.ServerId
            | None ->
                let! r = listNodes cluster 
                match r |> Seq.tryHead |> Option.map (fun h -> h.Server) with
                | Some s -> nodeId := s
                | None ->
                    failwithf "Could not get a flocker node!"
            let! res = flockerctl cluster "master-01" (sprintf "create -n %s -s %d -m \"name=%s,cluster=%s\"" !nodeId size name cluster)
            res |> Proc.failOnExitCode |> ignore
      }

    let clone fromCluster toCluster =
        ()


