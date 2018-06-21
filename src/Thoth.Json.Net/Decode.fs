module Thoth.Json.Net.Decode

open Newtonsoft.Json
open Newtonsoft.Json.Linq
open System.IO

module Helpers =

    let anyToString (token: JToken) : string =
        use stream = new StringWriter(NewLine = "\n")
        use jsonWriter = new JsonTextWriter(
                                stream,
                                Formatting = Formatting.Indented,
                                Indentation = 4 )

        token.WriteTo(jsonWriter)
        stream.ToString()

    let isBool (token: JToken) = token.Type = JTokenType.Boolean
    let isNumber (token: JToken) = token.Type = JTokenType.Float
    let isString (token: JToken) = token.Type = JTokenType.String
    let isArray (token: JToken) = token.Type = JTokenType.Array
    let isObject (token: JToken) = token.Type = JTokenType.Object
    let isNull (token: JToken) = token.Type = JTokenType.Null
    let asBool (token: JToken): bool = token.Value<bool>()
    let asInt (token: JToken): int = token.Value<int>()
    let asFloat (token: JToken): float = token.Value<float>()
    let asString (token: JToken): string = token.Value<string>()
    let asArray (token: JToken): JToken[] = token.Value<JArray>().Values() |> Seq.toArray

type DecoderError =
    | BadPrimitive of string * JToken
    | BadPrimitiveExtra of string * JToken * string
    | BadField of string * JToken
    | BadType of string * JToken
    | BadPath of string * JToken * string
    | TooSmallArray of string * JToken
    | FailMessage of string
    | BadOneOf of string list
    | Direct of string

type Decoder<'T> = JToken -> Result<'T, DecoderError>

let private genericMsg msg value newLine =
    try
        "Expecting "
            + msg
            + " but instead got:"
            + (if newLine then "\n" else " ")
            + (Helpers.anyToString value)
    with
        | _ ->
            "Expecting "
            + msg
            + " but decoder failed. Couldn't report given value due to circular structure."
            + (if newLine then "\n" else " ")

let private errorToString =
    function
    | BadPrimitive (msg, value) ->
        genericMsg msg value false
    | BadType (msg, value) ->
        genericMsg msg value true
    | BadPrimitiveExtra (msg, value, reason) ->
        genericMsg msg value false + "\nReason: " + reason
    | BadField (msg, value) ->
        genericMsg msg value true
    | BadPath (msg, value, fieldName) ->
        genericMsg msg value true + ("\nNode `" + fieldName + "` is unkown.")
    | TooSmallArray (msg, value) ->
        "Expecting " + msg + ".\n" + (Helpers.anyToString value)
    | BadOneOf messages ->
        "I run into the following problems:\n\n" + String.concat "\n" messages
    | FailMessage msg ->
        "I run into a `fail` decoder: " + msg
    | Direct msg ->
        msg

let unwrap (decoder : Decoder<'T>) (value : JToken) : 'T =
    match decoder value with
    | Ok success ->
        success
    | Error error ->
        failwith (errorToString error)

///////////////
// Runners ///
/////////////

let private decodeValueError (decoder : Decoder<'T>) =
    fun value ->
        try
            match decoder value with
            | Ok success ->
                Ok success
            | Error error ->
                Error error
        with
            | ex ->
                Error (Direct ex.Message)

let decodeValue (decoder : Decoder<'T>) =
    fun value ->
        match decodeValueError decoder value with
        | Ok success ->
            Ok success
        | Error error ->
            Error (errorToString error)

let decodeString (decoder : Decoder<'T>) =
    fun value ->
        try
            let json = Newtonsoft.Json.Linq.JValue.Parse value
            decodeValue decoder json
        with
            | ex ->
                Error("Given an invalid JSON: " + ex.Message)

//////////////////
// Primitives ///
////////////////

let string : Decoder<string> =
    fun token ->
        if Helpers.isString token then
            Ok(Helpers.asString token)
        else
            BadPrimitive("a string", token) |> Error

let guid : Decoder<System.Guid> =
    fun value ->
        // Using Helpers.isString fails because Json.NET directly assigns Guid type
        if value.Type = JTokenType.Guid
        then value.Value<System.Guid>() |> Ok
        else BadPrimitive("a guid", value) |> Error

let int : Decoder<int> =
    fun token ->
        if token.Type <> JTokenType.Integer then
            BadPrimitive("an int", token) |> Error
        else
            try
                Ok(Helpers.asInt token)
            with
                | _ -> BadPrimitiveExtra("an int", token, "Value was either too large or too small for an int") |> Error

let int64 : Decoder<int64> =
    fun value ->
        if Helpers.isNumber value
        then Helpers.asInt value |> int64 |> Ok
        elif Helpers.isString value
        then Helpers.asString value |> System.Int64.Parse |> Ok
        else BadPrimitive("an int64", value) |> Error

let uint64 : Decoder<uint64> =
    fun value ->
        if Helpers.isNumber value
        then Helpers.asInt value |> uint64 |> Ok
        elif Helpers.isString value
        then Helpers.asString value |> System.UInt64.Parse |> Ok
        else BadPrimitive("an uint64", value) |> Error

let bigint : Decoder<bigint> =
    fun value ->
        if Helpers.isNumber value
        then Helpers.asInt value |> bigint |> Ok
        elif Helpers.isString value
        then Helpers.asString value |> bigint.Parse |> Ok
        else BadPrimitive("a bigint", value) |> Error

let bool : Decoder<bool> =
    fun token ->
        if Helpers.isBool token then
            Ok(Helpers.asBool token)
        else
            BadPrimitive("a boolean", token) |> Error

let float : Decoder<float> =
    fun token ->
        if token.Type = JTokenType.Float then
            Ok(token.Value<float>())
        else if token.Type = JTokenType.Integer then
            Ok(token.Value<float>())
        else
            BadPrimitive("a float", token) |> Error

let decimal : Decoder<decimal> =
    fun value ->
        if Helpers.isNumber value
        then Helpers.asFloat value |> decimal |> Ok
        elif Helpers.isString value
        then Helpers.asString value |> System.Decimal.Parse |> Ok
        else BadPrimitive("a decimal", value) |> Error

let datetime : Decoder<System.DateTime> =
    fun value ->
        // Using Helpers.isString fails because Json.NET directly assigns Date type
        if value.Type = JTokenType.Date
        then value.Value<System.DateTime>() |> Ok
        else BadPrimitive("a date", value) |> Error

let datetimeOffset : Decoder<System.DateTimeOffset> =
    fun value ->
        // Using Helpers.isString fails because Json.NET directly assigns Date type
        if value.Type = JTokenType.Date
        then value.Value<System.DateTime>() |> System.DateTimeOffset |> Ok
        else BadPrimitive("a date with offset", value) |> Error

/////////////////////////
// Object primitives ///
///////////////////////


let field (fieldName: string) (decoder : Decoder<'value>) : Decoder<'value> =
    fun token ->
        if token.Type = JTokenType.Object then
            let fieldValue = token.Item(fieldName)
            if isNull fieldValue then
                BadField ("an object with a field named `" + fieldName + "`", token) |> Error
            else
                decoder fieldValue
        else
            BadType("an object", token)
            |> Error

exception UndefinedValueException of string
exception NonObjectTypeException

let at (fieldNames: string list) (decoder : Decoder<'value>) : Decoder<'value> =
    fun token ->
        let mutable cValue = token
        let mutable index = 0
        try
            for fieldName in fieldNames do
                if cValue.Type = JTokenType.Object then
                    let currentNode = cValue.Item(fieldName)
                    if isNull currentNode then
                        raise (UndefinedValueException fieldName)
                    else
                        cValue <- currentNode
                else
                    raise NonObjectTypeException
                index <- index + 1

            unwrap decoder cValue |> Ok
        with
            | NonObjectTypeException ->
                let path = String.concat "." fieldNames.[..index-1]
                BadType ("an object at `" + path + "`", cValue)
                |> Error
            | UndefinedValueException fieldName ->
                let msg = "an object with path `" + (String.concat "." fieldNames) + "`"
                BadPath (msg, token, fieldName)
                |> Error

let index (requestedIndex: int) (decoder : Decoder<'value>) : Decoder<'value> =
    fun token ->
        if token.Type = JTokenType.Array then
            let vArray = Helpers.asArray token
            if requestedIndex < vArray.Length then
                unwrap decoder (vArray.[requestedIndex]) |> Ok
            else
                let msg =
                    "a longer array. Need index `"
                        + (requestedIndex.ToString())
                        + "` but there are only `"
                        + (vArray.Length.ToString())
                        + "` entries"

                TooSmallArray(msg, token)
                |> Error
        else
            BadPrimitive("an array", token)
            |> Error

// let nullable (d1: Decoder<'value>) : Resul<'value option, DecoderError> =

// //////////////////////
// // Data structure ///
// ////////////////////

let list (decoder : Decoder<'value>) : Decoder<'value list> =
    fun token ->
        if token.Type = JTokenType.Array then
            Helpers.asArray token
            |> Seq.map (unwrap decoder)
            |> Seq.toList
            |> Ok
        else
            BadPrimitive ("a list", token)
            |> Error

let array (decoder : Decoder<'value>) : Decoder<'value array> =
    fun token ->
        if token.Type = JTokenType.Array then
            Helpers.asArray token
            |> Array.map (unwrap decoder)
            |> Ok
        else
            BadPrimitive ("an array", token)
            |> Error

let keyValuePairs (decoder : Decoder<'value>) : Decoder<(string * 'value) list> =
    fun token ->
        if token.Type = JTokenType.Object then
            let value = token.Value<JObject>()

            value.Properties()
            |> Seq.map (fun prop ->
                (prop.Name, value.SelectToken(prop.Name) |> unwrap decoder)
            )
            |> Seq.toList
            |> Ok
        else
            BadPrimitive ("an object", token)
            |> Error

let tuple2 (decoder1: Decoder<'T1>) (decoder2: Decoder<'T2>) : Decoder<'T1 * 'T2> =
    fun token ->
        if token.Type = JTokenType.Array then
            let values = Helpers.asArray token
            let a = unwrap decoder1 values.[0]
            let b = unwrap decoder2 values.[1]
            Ok(a, b)
        else
            BadPrimitive ("a tuple", token)
            |> Error

//////////////////////////////
// Inconsistent Structure ///
////////////////////////////

let option (d1 : Decoder<'value>) : Decoder<'value option> =
    fun value ->
        if Helpers.isNull value
        then Ok None
        else
            // TODO: Review, is this OK?
            match d1 value with
            | Ok v -> Some v |> Ok
            | Error(BadField _) -> Ok None
            | Error er -> Error er

let oneOf (decoders : Decoder<'value> list) : Decoder<'value> =
    fun value ->
        let rec runner (decoders : Decoder<'value> list) (errors : string list) =
            match decoders with
            | head::tail ->
                match decodeValue head value with
                | Ok v ->
                    Ok v
                | Error error -> runner tail (errors @ [error])
            | [] -> BadOneOf errors |> Error

        runner decoders []

//////////////////////
// Fancy decoding ///
////////////////////

let nil (output : 'a) : Decoder<'a> =
    fun token ->
        if token.Type = JTokenType.Null then
            Ok output
        else
            BadPrimitive("null", token) |> Error

let value v = Ok v

let succeed (output : 'a) : Decoder<'a> =
    fun _ ->
        Ok output

let fail (msg: string) : Decoder<'a> =
    fun _ ->
        FailMessage msg |> Error

let andThen (cb: 'a -> Decoder<'b>) (decoder : Decoder<'a>) : Decoder<'b> =
    fun value ->
        match decodeValue decoder value with
        | Error error -> failwith error
        | Ok result ->
            cb result value

/////////////////////
// Map functions ///
///////////////////

let map
    (ctor : 'a -> 'value)
    (d1 : Decoder<'a>) : Decoder<'value> =
    (fun value ->
        let t = unwrap d1 value
        Ok (ctor t)
    )

let map2
    (ctor : 'a -> 'b -> 'value)
    (d1 : Decoder<'a>)
    (d2 : Decoder<'b>) : Decoder<'value> =
    (fun value ->
        let t = unwrap d1 value
        let t2 = unwrap d2 value

        Ok (ctor t t2)
    )

let map3
    (ctor : 'a -> 'b -> 'c -> 'value)
    (d1 : Decoder<'a>)
    (d2 : Decoder<'b>)
    (d3 : Decoder<'c>) : Decoder<'value> =
    (fun value ->
        let v1 = unwrap d1 value
        let v2 = unwrap d2 value
        let v3 = unwrap d3 value

        Ok (ctor v1 v2 v3)
    )

let map4
    (ctor : 'a -> 'b -> 'c -> 'd -> 'value)
    (d1 : Decoder<'a>)
    (d2 : Decoder<'b>)
    (d3 : Decoder<'c>)
    (d4 : Decoder<'d>) : Decoder<'value> =
    (fun value ->
        let v1 = unwrap d1 value
        let v2 = unwrap d2 value
        let v3 = unwrap d3 value
        let v4 = unwrap d4 value

        Ok (ctor v1 v2 v3 v4)
    )

let map5
    (ctor : 'a -> 'b -> 'c -> 'd -> 'e -> 'value)
    (d1 : Decoder<'a>)
    (d2 : Decoder<'b>)
    (d3 : Decoder<'c>)
    (d4 : Decoder<'d>)
    (d5 : Decoder<'e>) : Decoder<'value> =
    (fun value ->
        let v1 = unwrap d1 value
        let v2 = unwrap d2 value
        let v3 = unwrap d3 value
        let v4 = unwrap d4 value
        let v5 = unwrap d5 value

        Ok (ctor v1 v2 v3 v4 v5)
    )

let map6
    (ctor : 'a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'value)
    (d1 : Decoder<'a>)
    (d2 : Decoder<'b>)
    (d3 : Decoder<'c>)
    (d4 : Decoder<'d>)
    (d5 : Decoder<'e>)
    (d6 : Decoder<'f>) : Decoder<'value> =
    (fun value ->
        let v1 = unwrap d1 value
        let v2 = unwrap d2 value
        let v3 = unwrap d3 value
        let v4 = unwrap d4 value
        let v5 = unwrap d5 value
        let v6 = unwrap d6 value

        Ok (ctor v1 v2 v3 v4 v5 v6)
    )

let map7
    (ctor : 'a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'g -> 'value)
    (d1 : Decoder<'a>)
    (d2 : Decoder<'b>)
    (d3 : Decoder<'c>)
    (d4 : Decoder<'d>)
    (d5 : Decoder<'e>)
    (d6 : Decoder<'f>)
    (d7 : Decoder<'g>) : Decoder<'value> =
    (fun value ->
        let v1 = unwrap d1 value
        let v2 = unwrap d2 value
        let v3 = unwrap d3 value
        let v4 = unwrap d4 value
        let v5 = unwrap d5 value
        let v6 = unwrap d6 value
        let v7 = unwrap d7 value

        Ok (ctor v1 v2 v3 v4 v5 v6 v7)
    )

let map8
    (ctor : 'a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'g -> 'h -> 'value)
    (d1 : Decoder<'a>)
    (d2 : Decoder<'b>)
    (d3 : Decoder<'c>)
    (d4 : Decoder<'d>)
    (d5 : Decoder<'e>)
    (d6 : Decoder<'f>)
    (d7 : Decoder<'g>)
    (d8 : Decoder<'h>) : Decoder<'value> =
        (fun value ->
            let v1 = unwrap d1 value
            let v2 = unwrap d2 value
            let v3 = unwrap d3 value
            let v4 = unwrap d4 value
            let v5 = unwrap d5 value
            let v6 = unwrap d6 value
            let v7 = unwrap d7 value
            let v8 = unwrap d8 value

            Ok (ctor v1 v2 v3 v4 v5 v6 v7 v8)
        )

let dict (decoder : Decoder<'value>) : Decoder<Map<string, 'value>> =
    map Map.ofList (keyValuePairs decoder)

////////////////
// Pipeline ///
//////////////

let custom d1 d2 = map2 (|>) d1 d2

let hardcoded<'a, 'b> : 'a -> Decoder<('a -> 'b)> -> JToken -> Result<'b,DecoderError> = succeed >> custom

let required (key : string) (valDecoder : Decoder<'a>) (decoder : Decoder<'a -> 'b>) : Decoder<'b> =
    custom (field key valDecoder) decoder

let requiredAt (path : string list) (valDecoder : Decoder<'a>) (decoder : Decoder<'a -> 'b>) : Decoder<'b> =
    custom (at path valDecoder) decoder

let decode output value = succeed output value

/// Convert a `Decoder<Result<x, 'a>>` into a `Decoder<'a>`
let resolve d1 : Decoder<'a> =
    fun value ->
        andThen id d1 value

let optionalDecoder pathDecoder valDecoder fallback =
    let nullOr decoder =
        oneOf [ decoder; nil fallback ]

    let handleResult input =
        match decodeValueError pathDecoder input with
        | Ok rawValue ->
            // Field was present, so we try to decode the value
            match decodeValue (nullOr valDecoder) rawValue with
            | Ok finalResult ->
                succeed finalResult

            | Error finalErr ->
                fail finalErr

        | Error ((BadType _ ) as errorInfo) ->
            // If the error is of type `BadType` coming from `at` decoder then return the error
            // This mean the json was expecting an object but got an array instead
            fun _ -> Error errorInfo
        | Error _ ->
            // Field was not present && type was valid
            succeed fallback

    value
    |> andThen handleResult

let optional key valDecoder fallback decoder =
    fun (token : JToken) ->
        if token.Type = JTokenType.Object then
            custom (optionalDecoder (field key value) valDecoder fallback) decoder token
        else
            BadType("an object", token)
            |> Error


let optionalAt path valDecoder fallback decoder =
    fun (token : JToken) ->
        if token.Type = JTokenType.Object then
            custom (optionalDecoder (at path value) valDecoder fallback) decoder token
        else
            BadType("an object", token)
            |> Error

//////////////////
// Reflection ///
////////////////

open Microsoft.FSharp.Reflection

[<AbstractClass>]
type private BoxedDecoder() =
    abstract Decode: token: JToken -> Result<obj, DecoderError>
    member this.BoxedDecoder: Decoder<obj> =
        fun token -> this.Decode(token)

type private DecoderCrate<'T>(dec: Decoder<'T>) =
    inherit BoxedDecoder()
    override __.Decode(token) =
        match dec token with
        | Ok v -> Ok(box v)
        | Error er -> Error er
    member __.UnboxedDecoder = dec

let inline private boxDecoder (d: Decoder<'T>): BoxedDecoder =
    DecoderCrate(d) :> BoxedDecoder

let inline private unboxDecoder<'T> (d: BoxedDecoder): Decoder<'T> =
    (d :?> DecoderCrate<'T>).UnboxedDecoder

let private object (decoders: (string * BoxedDecoder)[]) (value: JToken) =
    if not (Helpers.isObject value) then
        BadPrimitive ("an object", value) |> Error
    else
        (decoders, Ok []) ||> Array.foldBack (fun (name, decoder) acc ->
            match acc with
            | Error _ -> acc
            | Ok result ->
                // TODO!!! Optional types shouldn't be required
                field name (decoder.BoxedDecoder) value
                |> Result.map (fun v -> v::result))

let private mixedArray msg (decoders: BoxedDecoder[]) (values: JToken[]): Result<obj list, DecoderError> =
    if decoders.Length <> values.Length then
        sprintf "Expected %i %s but got %i" decoders.Length msg values.Length
        |> FailMessage |> Error
    else
        (values, decoders, Ok [])
        |||> Array.foldBack2 (fun value decoder acc ->
            match acc with
            | Error _ -> acc
            | Ok result -> decoder.Decode value |> Result.map (fun v -> v::result))

let private genericOption t (decoder: BoxedDecoder) =
    fun (value: JToken) ->
        // TODO: Is GetUnionCases a costly operation? Should we cache this?
        let ucis = FSharpType.GetUnionCases(t)
        if Helpers.isNull value
        then FSharpValue.MakeUnion(ucis.[0], [||]) |> Ok
        else decoder.Decode value |> Result.map (fun v ->
            FSharpValue.MakeUnion(ucis.[1], [|v|]))

let private genericList t (decoder: BoxedDecoder) =
    fun (value: JToken) ->
        if not (Helpers.isArray value) then
            BadPrimitive ("a list", value) |> Error
        else
            let values = value.Value<JArray>()
            let ucis = FSharpType.GetUnionCases(t)
            let empty = FSharpValue.MakeUnion(ucis.[0], [||])
            (values, Ok empty) ||> Seq.foldBack (fun value acc ->
                match acc with
                | Error _ -> acc
                | Ok acc ->
                    match decoder.Decode value with
                    | Error er -> Error er
                    | Ok result -> FSharpValue.MakeUnion(ucis.[1], [|result; acc|]) |> Ok)

let rec private makeUnion t isCamelCase name (values: JToken[]) =
    match FSharpType.GetUnionCases(t) |> Array.tryFind (fun x -> x.Name = name) with
    | None -> FailMessage("Cannot find case " + name + " in " + t.FullName) |> Error
    | Some uci ->
        if values.Length = 0 then
            FSharpValue.MakeUnion(uci, [||]) |> Ok
        else
            let decoders = uci.GetFields() |> Array.map (fun fi -> autoDecoder isCamelCase fi.PropertyType)
            mixedArray "union fields" decoders values
            |> Result.map (fun values -> FSharpValue.MakeUnion(uci, List.toArray values))

and private autoDecodeRecordsAndUnions (t: System.Type) (isCamelCase : bool) : BoxedDecoder =
    if FSharpType.IsRecord(t) then
        boxDecoder(fun value ->
            let decoders =
                FSharpType.GetRecordFields(t)
                |> Array.map (fun fi ->
                    let name =
                        if isCamelCase then
                            fi.Name.[..0].ToLowerInvariant() + fi.Name.[1..]
                        else
                            fi.Name
                    name, autoDecoder isCamelCase fi.PropertyType)
            object decoders value
            |> Result.map (fun xs -> FSharpValue.MakeRecord(t, List.toArray xs)))
    elif FSharpType.IsUnion(t) then
        boxDecoder(fun (value: JToken) ->
            if Helpers.isString(value) then
                let name = Helpers.asString value
                makeUnion t isCamelCase name [||]
            elif Helpers.isArray(value) then
                let values = Helpers.asArray value
                let name = Helpers.asString values.[0]
                makeUnion t isCamelCase name values.[1..]
            else BadPrimitive("a string or array", value) |> Error)
    else
        failwithf "Class types cannot be automatically deserialized: %s" t.FullName

and private autoDecoder isCamelCase (t: System.Type) : BoxedDecoder =
    if t.IsArray then
        let elemType = t.GetElementType()
        let decoder = autoDecoder isCamelCase elemType
        boxDecoder(fun value ->
            match array decoder.BoxedDecoder value with
            | Ok items ->
                let ar = System.Array.CreateInstance(elemType, items.Length)
                for i = 0 to ar.Length - 1 do
                    ar.SetValue(items.[i], i)
                Ok ar
            | Error er -> Error er)
    elif t.IsGenericType then
        if FSharpType.IsTuple(t) then
            let decoders = FSharpType.GetTupleElements(t) |> Array.map (autoDecoder isCamelCase)
            boxDecoder(fun value ->
                if Helpers.isArray value then
                    mixedArray "tuple elements" decoders (Helpers.asArray value)
                    |> Result.map (fun xs -> FSharpValue.MakeTuple(List.toArray xs, t))
                else BadPrimitive ("an array", value) |> Error)
        else
            let fullname = t.GetGenericTypeDefinition().FullName
            if fullname = typedefof<obj option>.FullName
            then autoDecoder isCamelCase t.GenericTypeArguments.[0] |> genericOption t |> boxDecoder
            elif fullname = typedefof<obj list>.FullName
            then autoDecoder isCamelCase t.GenericTypeArguments.[0] |> genericList t |> boxDecoder
            elif fullname = typedefof< Map<string, obj> >.FullName
            then
                let decoder = t.GenericTypeArguments.[1] |> autoDecoder isCamelCase
                (array (tuple2 string decoder.BoxedDecoder) >> Result.map Map) |> boxDecoder // TODO: fails
            else autoDecodeRecordsAndUnions t isCamelCase
    else
        let fullname = t.FullName
        // TODO: Fable will compile typeof<bool>.FullName as strings
        // but for .NET maybe it's better to cache a dictionary
        if fullname = typeof<bool>.FullName
        then boxDecoder bool
        elif fullname = typeof<string>.FullName
        then boxDecoder string
        elif fullname = typeof<int>.FullName
        then boxDecoder int
        elif fullname = typeof<float>.FullName
        then boxDecoder float
        elif fullname = typeof<decimal>.FullName
        then boxDecoder decimal
        elif fullname = typeof<int64>.FullName
        then boxDecoder int64
        elif fullname = typeof<uint64>.FullName
        then boxDecoder uint64
        elif fullname = typeof<bigint>.FullName
        then boxDecoder bigint
        elif fullname = typeof<System.DateTime>.FullName
        then boxDecoder datetime
        elif fullname = typeof<System.DateTimeOffset>.FullName
        then boxDecoder datetimeOffset
        elif fullname = typeof<System.Guid>.FullName
        then boxDecoder guid
        elif fullname = typeof<obj>.FullName
        then boxDecoder Ok
        else autoDecodeRecordsAndUnions t isCamelCase

type Auto =
    static member GenerateDecoder<'T> (?isCamelCase : bool): Decoder<'T> =
        let serializer = JsonSerializer()
        serializer.Converters.Add(Converters.CacheConverter.Singleton)
        if defaultArg isCamelCase false then
            serializer.ContractResolver <- new Serialization.CamelCasePropertyNamesContractResolver()

        fun token ->
            token.ToObject<'T>(serializer) |> Ok

    static member DecodeString<'T>(json: string, ?isCamelCase : bool): 'T =
        // let settings = JsonSerializerSettings(Converters = [|Converters.CacheConverter.Singleton|])
        // if defaultArg isCamelCase false then
        //     settings.ContractResolver <- new Serialization.CamelCasePropertyNamesContractResolver()

        // JsonConvert.DeserializeObject<'T>(json, settings)

        let isCamelCase = defaultArg isCamelCase false
        let decoder = autoDecoder isCamelCase typeof<'T>
        match decodeString decoder.BoxedDecoder json with
        | Ok x -> x :?> 'T
        | Error msg -> failwith msg

    static member DecodeString(json: string, t: System.Type, ?isCamelCase : bool): obj =
        let settings = JsonSerializerSettings(Converters = [|Converters.CacheConverter.Singleton|])
        if defaultArg isCamelCase false then
            settings.ContractResolver <- new Serialization.CamelCasePropertyNamesContractResolver()

        JsonConvert.DeserializeObject(json, t, settings)
