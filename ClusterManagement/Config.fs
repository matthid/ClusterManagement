namespace ClusterManagement

open System.IO

module Config =
    let getTokens clusterName cc =
        let rawTokens = ClusterConfig.getTokens cc
        Seq.append rawTokens [ { ClusterConfig.Token.Name = "CLUSTER_NAME"; Value = clusterName}]

    let replaceTokens tokens text =
      tokens
      |> Seq.fold (fun (state:string) (token:ClusterConfig.Token) ->
          state.Replace(sprintf "__%s__" token.Name, token.Value)) text

    let replaceTokensInFile tokens f =
      printfn "Replacing tokens in %s" f
      let fileText = File.ReadAllText f
      let replaced = replaceTokens tokens fileText
      if fileText = replaced then
        eprintfn "No tokens where found in '%s', have %A. Text was: %s" f (tokens |> Seq.toList) fileText
      File.WriteAllText(f, replaced)

    let set clusterName key value =
        //Storage.openClusterWithStoredSecret clusterName
        ClusterConfig.readClusterConfig clusterName
        |> ClusterConfig.setConfig key value
        |> ClusterConfig.writeClusterConfig clusterName
        Storage.quickSaveClusterWithStoredSecret clusterName

    let ensureConfig clusterName n cc =
        match ClusterConfig.getConfig n cc with
        | None | Some "" | Some null ->
            failwithf "Config '%s' is required to initialize the cluster. Use 'ClusterManagement config --cluster \"%s\" set %s <my-value>' to set a value." n clusterName n
        | _ -> ()

    let cloneConfig sourceCluster destCluster =

        let dest = ClusterConfig.readClusterConfig destCluster
        ClusterConfig.readClusterConfig sourceCluster
        |> ClusterConfig.getTokens
        |> Seq.fold (fun state tok -> ClusterConfig.setConfig tok.Name tok.Value state) dest
        |> ClusterConfig.writeClusterConfig destCluster

(*
open FSharp.Configuration

// Assume /tok is mounted with the tokenized files
// Assume /tokens.secret is the token file
let [<Literal>] tokensFile = @"tokens.yaml"
let [<Literal>] tokensDir = @"tok"
let text = File.ReadAllText tokensFile

type Tokens = YamlConfig< tokensFile >

let t = new Tokens()
t.LoadText text
let toReplace =
  t.tokens.GetType().GetProperties()
  |> Seq.map (fun p -> sprintf "__%s__" p.Name, p.GetValue(t.tokens).ToString())

let rec findFiles d =
  Directory.EnumerateDirectories(d)
  |> Seq.collect (findFiles)
  |> (fun di -> [ di; Directory.EnumerateFiles(d) ] |> Seq.collect id)

findFiles (tokensDir)
|> Seq.iter replaceTokensInFile

*)