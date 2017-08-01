namespace ClusterManagement

[<AutoOpen>]
module Output =
    let private secrets = new System.Collections.Concurrent.ConcurrentBag<string * string>()
    let addSecret secret replacement =
        secrets.Add(secret, replacement)
    
    let stripSecrets s =
        secrets
        |> Seq.fold (fun (s:string) (secret, repl) ->
            s.Replace(secret, repl)) s
    let private safePrint includeNew s =
        let safe = stripSecrets s
        if includeNew then
            Printf.printfn "%s" safe
        else Printf.printf "%s" safe
    let printfn (f: Printf.StringFormat<'t, unit>) : 't = 
        Printf.ksprintf (safePrint true) f 
    let printf (f: Printf.StringFormat<'t, unit>) : 't =
        Printf.ksprintf (safePrint false) f
    