﻿#r @"..\..\..\packages\FSharp.Compiler.Service\lib\net45\FSharp.Compiler.Service.dll"

open Microsoft.FSharp.Compiler.SourceCodeServices
    
let header = """// Generated by genTransformer.fsx. Do not modify.

module FCSTest.Transformer

open Microsoft.FSharp.Compiler.SourceCodeServices
            
open FCSTest.InnerTransformers

let rec private tr (env: Environment) (e: FSharpExpr) =
    match e with
"""

let intfHeader = """// Generated by genTransformer.fsx. Do not modify.

module FCSTest.InnerTransformers

open Microsoft.FSharp.Compiler.SourceCodeServices

open CommonAST.AST

type [<Sealed>] Environment =
    static member Empty : Environment with get

"""

let codeGen = System.Text.StringBuilder(header)
let intfGen = System.Text.StringBuilder(intfHeader)
let implGen = System.Text.StringBuilder()

let inline cprintf x = Printf.bprintf codeGen x
let inline iprintf x = Printf.bprintf intfGen x
let inline mprintf x = Printf.bprintf implGen x
                    
type FST = FSharp.Reflection.FSharpType

let getArguments (t: System.Type) =
    if FST.IsTuple t then
        FST.GetTupleElements t
    else [| t |]

let abc (i: int) = 'a' + char i |> string

typeof<FSharpExpr>.Assembly.GetType("Microsoft.FSharp.Compiler.SourceCodeServices.BasicPatterns").GetMembers()
|> Seq.filter (fun m -> m.Name.StartsWith "|")
|> Seq.map (fun m -> m.Name.[1 .. m.Name.Length - 4])
//|> Seq.iter (printfn "%s") 

|> Seq.iter (fun m -> 
    let m = m :?> System.Reflection.MethodInfo
    let name = m.Name.[1 .. m.Name.Length - 4]
    let args = m.ReturnType.GetGenericArguments().[0] |> getArguments
    let na = args.Length
    
    let removeParens (s: string) =
        if s.[0] = '(' && s.[s.Length - 1] = ')' then s.[1 .. s.Length - 2] else s

    let rec trf (a: System.Type) =
        if a.Name = "FSharpExpr" then 
            Some "(tr ienv)", "Expr"   
        elif a.Name = "FSharpObjectExprOverride" then
            Some "(fun o -> o, tr ienv o.Body)", "(FSharpObjectExprOverride * Expr)" 
        elif a.Name.StartsWith "FSharpList" then
            let ai = a.GetGenericArguments().[0]
            let tr, r = trf ai
            tr |> Option.map (fun tr -> "List.map " + tr), "list<" + removeParens r + ">"  
        elif a.Name.StartsWith "FSharpOption" then
            let ai = a.GetGenericArguments().[0]
            let tr, r = trf ai
            tr |> Option.map (fun tr -> "Option.map " + tr), "option<" + removeParens r + ">"  
        elif FST.IsTuple a then
            match FST.GetTupleElements a with
            | [| a1; a2 |] ->
                let tr1, r1 = trf a1
                let tr2, r2 = trf a2
                match tr1, tr2 with
                | None, None -> None
                | _ ->
                    let tr1 = match tr1 with | Some t -> " |> " + t | _ -> ""
                    let tr2 = match tr2 with | Some t -> " |> " + t | _ -> ""
                    Some (sprintf "(fun (x, y) -> x%s, y%s)" tr1 tr2)
                , sprintf "(%s * %s)" r1 r2   
            | _ -> failwith "bigger tuple than 2 found"            
        else
            None, 
            match a.Name with
            | "Boolean" -> "bool"
            | "Object" -> "obj"
            | "String" -> "string"
            | "Int32" -> "int"
            | n -> n
    
    let args =
        args |> Seq.mapi (fun i a ->
            let tr, r = trf a
            match tr with
            | Some t -> sprintf "(fun ienv -> %s |> %s)" (abc i) t, sprintf "(Environment -> %s)" r
            | None -> abc i, r
        )

    cprintf "    | BasicPatterns.%s(%s) -> transform%s env %s\n" 
        name (String.concat ", " (Seq.init na abc)) name 
        (String.concat " " (args |> Seq.map fst))
    iprintf "val transform%s : Environment -> %s -> Expr\n" name (String.concat " -> " (args |> Seq.map snd))
    mprintf "let transform%s (env: Environment) %s =\n" name (String.concat " " (args |> Seq.mapi (fun i (_, t) -> sprintf "(%s: %s)" (abc i) t)))
    mprintf "    failwith<Expr> \"transform%s: Not implemented\"\n\n" name
)

cprintf "    | _ -> failwith \"FSharpExpr not covered by BasicPatterns\"\n\n" 
cprintf "let transform (e: FSharpExpr) = tr Environment.Empty e"

System.IO.File.WriteAllText(__SOURCE_DIRECTORY__ + @"\Transformer.fs", string codeGen)
System.IO.File.WriteAllText(__SOURCE_DIRECTORY__ + @"\InnerTransformers.fsi", string intfGen)
