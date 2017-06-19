namespace ClusterManagement

open System.IO.Compression
open System.IO
open System
open System.Text
open System.Security.Cryptography


// Taken & Converted From http://stackoverflow.com/questions/10168240/encrypting-decrypting-a-string-in-c-sharp
module CypherHelper =
  open System
  open System.Text
  open System.Security.Cryptography
  open System.IO
  open System.Linq
  // This constant is used to determine the keysize of the encryption algorithm in bits.
  // We divide this by 8 within the code below to get the equivalent number of bytes.
  let private keysize = 256

  // This constant determines the number of iterations for the password bytes generation function.
  let private derivationIterations = 1000

  let private generate256BitsOfRandomEntropy () =
    let randomBytes = Array.zeroCreate 32 // 32 Bytes will give us 256 bits.
    use rngCsp = new RNGCryptoServiceProvider()
    // Fill the array with cryptographically secure random bytes.
    rngCsp.GetBytes(randomBytes)
    randomBytes

  let encrypt (passPhrase:string) (sourceStream:Stream) (targetStream:Stream) =
    // Salt and IV is randomly generated each time, but is preprended to encrypted cipher text
    // so that the same Salt and IV values can be used when decrypting.
    let saltStringBytes = generate256BitsOfRandomEntropy()
    let ivStringBytes = generate256BitsOfRandomEntropy()
    use password = new Rfc2898DeriveBytes(passPhrase, saltStringBytes, derivationIterations)
    let keyBytes = password.GetBytes(keysize / 8)
    use symmetricKey = new RijndaelManaged()
    symmetricKey.BlockSize <- 256
    symmetricKey.Mode <- CipherMode.CBC
    symmetricKey.Padding <- PaddingMode.PKCS7
    use encryptor = symmetricKey.CreateEncryptor(keyBytes, ivStringBytes)

    // Create the final bytes as a concatenation of the random salt bytes, the random iv bytes and the cipher bytes.
    targetStream.Write(saltStringBytes, 0, saltStringBytes.Length)
    targetStream.Write(ivStringBytes, 0, ivStringBytes.Length)

    use cryptoStream = new CryptoStream(targetStream, encryptor, CryptoStreamMode.Write)

    sourceStream.CopyTo(cryptoStream)
    cryptoStream.FlushFinalBlock()
    cryptoStream.Flush()

    cryptoStream.Close()

  let encryptString passPhrase (text:string) =
    let mem = new MemoryStream()
    encrypt passPhrase (new MemoryStream(Encoding.UTF8.GetBytes(text))) mem
    Convert.ToBase64String(mem.ToArray())

  let decrypt (passPhrase:string) (sourceStream:Stream) (targetStream:Stream) =
    let readBytes num =
        let b = Array.zeroCreate num
        let n = sourceStream.Read(b, 0, num)
        assert (n = num)
        b

    // Get the complete stream of bytes that represent:
    // [32 bytes of Salt] + [32 bytes of IV] + [n bytes of CipherText]
    // Get the saltbytes by extracting the first 32 bytes from the supplied cipherText bytes.
    let saltStringBytes = readBytes (keysize / 8)
    // Get the IV bytes by extracting the next 32 bytes from the supplied cipherText bytes.
    let ivStringBytes = readBytes (keysize / 8)
    // Get the actual cipher text bytes by removing the first 64 bytes from the cipherText string.

    use password = new Rfc2898DeriveBytes(passPhrase, saltStringBytes, derivationIterations)
    let keyBytes = password.GetBytes(keysize / 8)
    use symmetricKey = new RijndaelManaged()
    symmetricKey.BlockSize <- 256;
    symmetricKey.Mode <- CipherMode.CBC;
    symmetricKey.Padding <- PaddingMode.PKCS7;
    use decryptor = symmetricKey.CreateDecryptor(keyBytes, ivStringBytes)

    use cryptoStream = new CryptoStream(sourceStream, decryptor, CryptoStreamMode.Read)

    cryptoStream.CopyTo(targetStream)
    cryptoStream.Close()

  let decryptString passPhrase (cipherText:string) =
    let mem = new MemoryStream()
    decrypt passPhrase (new MemoryStream(Convert.FromBase64String(cipherText))) mem
    Encoding.UTF8.GetString(mem.ToArray())

module Zip =
    let createZip targetFile folder =
        ZipFile.CreateFromDirectory(folder, targetFile)
    let extractZip targetFolder zipFile =
        if Directory.EnumerateFileSystemEntries targetFolder |> Seq.isEmpty |> not then
            for fsi in Directory.EnumerateFileSystemEntries targetFolder do
                if File.Exists fsi then
                    if Env.isVerbose then
                        eprintfn "WARN: Removing file '%s' because it might be a leftover from a previously failed operation" fsi
                    File.Delete fsi
                if Directory.Exists fsi then
                    if Env.isVerbose then
                        eprintfn "WARN: Removing directory '%s' because it might be a leftover from a previously failed operation" fsi
                    Directory.Delete (fsi, true)

        ZipFile.ExtractToDirectory(zipFile, targetFolder)

    let encryptFile outputFile password inputFile =
        use fsOut = new FileStream(outputFile, FileMode.Create)
        use fsIn = new FileStream(inputFile, FileMode.Open)
        CypherHelper.encrypt password fsIn fsOut

    let decryptFile (outputFile:string) password inputFile  =
        use fsOut = new FileStream(outputFile, FileMode.Create)
        use fsIn = new FileStream(inputFile, FileMode.Open)
        CypherHelper.decrypt password fsIn fsOut

    let zipAndEncrypt outputFile password inputFolder =
        let tmpFile = sprintf "%s.tmp" outputFile
        if File.Exists tmpFile then File.Delete tmpFile
        try
            createZip tmpFile inputFolder
            if File.Exists outputFile then File.Delete outputFile
            encryptFile outputFile password tmpFile
        finally
            try
                if File.Exists tmpFile then File.Delete tmpFile
            with :? System.IO.IOException as e ->
                if Env.isVerbose then
                    eprintfn "Error while deleting temp file: %O" e

    let decryptAndUnzip outputFolder password inputFile =
        let tmpFile = sprintf "%s.tmp" inputFile
        if File.Exists tmpFile then File.Delete tmpFile
        try
            decryptFile tmpFile password inputFile
            extractZip outputFolder tmpFile 
        finally
            try
                if File.Exists tmpFile then File.Delete tmpFile
            with :? System.IO.IOException as e ->
                if Env.isVerbose then
                    eprintfn "Error while deleting temp file: %O" e