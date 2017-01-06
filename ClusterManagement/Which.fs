namespace ClusterManagement

module Which =
    let getTooPath toolName =
      async {
        let! toolPath =
            Proc.startProcess "/usr/bin/which" toolName
            |> Async.map (Proc.failWithMessage (sprintf "Tool '%s' was not found with which! Make sure it is installed." toolName))
            |> Async.map Proc.getStdOut
        return
            if toolPath.EndsWith("\n") then toolPath.Substring(0, toolPath.Length - 1)
            else toolPath
      }