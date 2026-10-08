using System.Text.RegularExpressions;

namespace LeanStudio.Core.Projects;

/// <summary>
/// Lean Studio's own project templates: a small verified program to start from, laid out the way verified code is
/// kept honest. Each has a specification (what the code should do, written so it is obviously right), an
/// implementation (what runs), proofs that the implementation does what the specification says, known-answer tests,
/// a README that explains the layout, and the assurance check in CI. Every template builds with no <c>sorry</c>, no
/// axiom and no <c>native_decide</c>, and every definition in it has a theorem about it.
/// </summary>
public static partial class ProjectTemplates
{
    /// <summary>One of Lean Studio's own templates (as opposed to <c>lake new</c>'s).</summary>
    public static bool IsVerified(ProjectTemplate t) => t is ProjectTemplate.VerifiedCrypto or ProjectTemplate.VerifiedParser or ProjectTemplate.VerifiedFileFormat;

    /// <summary>The template's name in the New Project dialog.</summary>
    public static string Title(ProjectTemplate t) => t switch
    {
        ProjectTemplate.VerifiedCrypto => "Verified crypto primitive (modular exponentiation)",
        ProjectTemplate.VerifiedParser => "Verified parser (with a proved round trip)",
        ProjectTemplate.VerifiedFileFormat => "Verified file format (encode, decode, proved inverse)",
        _ => t.ToString(),
    };

    [GeneratedRegex(@"\[\[lean_lib\]\]\s*\n\s*name\s*=\s*""(?<name>[^""]+)""")]
    private static partial Regex LibName();

    /// <summary>
    /// Fill a project <c>lake new … lib</c> just made with the template's files, in its library's namespace: the
    /// specification, implementation, proofs and tests modules, the library's root, a README, and (given Lean
    /// Studio's version) the assurance workflow. <c>lake new</c>'s placeholder module is removed.
    /// </summary>
    /// <returns>The files written, the specification first.</returns>
    public static IReadOnlyList<string> Write(LeanProject project, ProjectTemplate template, string? studioVersion = null)
    {
        string lakefile = Path.Combine(project.Root, "lakefile.toml");
        string lib = File.Exists(lakefile) && LibName().Match(File.ReadAllText(lakefile)) is { Success: true } m
            ? m.Groups["name"].Value
            : throw new InvalidOperationException("The new project has no [[lean_lib]] in lakefile.toml.");
        (string Spec, string Impl, string Proofs, string Tests, string Readme) files = template switch
        {
            ProjectTemplate.VerifiedCrypto => (CryptoSpec, CryptoImpl, CryptoProofs, CryptoTests, CryptoReadme),
            ProjectTemplate.VerifiedParser => (ParserSpec, ParserImpl, ParserProofs, ParserTests, ParserReadme),
            ProjectTemplate.VerifiedFileFormat => (FormatSpec, FormatImpl, FormatProofs, FormatTests, FormatReadme),
            _ => throw new ArgumentOutOfRangeException(nameof(template), template, "not one of Lean Studio's own templates"),
        };
        string dir = Path.Combine(project.Root, lib);
        Directory.CreateDirectory(dir);
        string placeholder = Path.Combine(dir, "Basic.lean");
        if (File.Exists(placeholder))
        {
            File.Delete(placeholder);
        }
        string Fill(string text) => text.Replace("{{Lib}}", lib, StringComparison.Ordinal).Replace("{{Package}}", Path.GetFileName(project.Root), StringComparison.Ordinal);
        var written = new List<string>();
        foreach ((string module, string text) in new[] { ("Spec", files.Spec), ("Impl", files.Impl), ("Proofs", files.Proofs), ("Tests", files.Tests) })
        {
            string path = Path.Combine(dir, module + ".lean");
            File.WriteAllText(path, Fill(text));
            written.Add(path);
        }
        string root = Path.Combine(project.Root, lib + ".lean");
        File.WriteAllText(root, Fill($"""
            -- The root of the `{lib}` library: everything that is built and checked.
            import {lib}.Spec
            import {lib}.Impl
            import {lib}.Proofs
            import {lib}.Tests

            """));
        written.Add(root);
        string readme = Path.Combine(project.Root, "README.md");
        File.WriteAllText(readme, Fill(files.Readme) + Fill(ReadmeLayout));
        written.Add(readme);
        if (studioVersion is not null)
        {
            if (Git.GitHub.AddAssuranceWorkflow(project.Root, studioVersion) is string workflow)
            {
                written.Add(workflow);
            }
        }
        return written;
    }

    // ---- shared README ----

    private const string ReadmeLayout = """

        ## How it is laid out

        | File | What it holds |
        |---|---|
        | `{{Lib}}/Spec.lean` | **What the code should do**, written to be obviously right rather than fast. Read this first: it is what everything else is proved against. |
        | `{{Lib}}/Impl.lean` | **What runs**: the version written for speed or for real inputs. |
        | `{{Lib}}/Proofs.lean` | **Why they agree**: theorems that the implementation does exactly what the specification says, for every input. |
        | `{{Lib}}/Tests.lean` | **Known answers**: `#guard` checks against published values. A test cannot prove the code right, but it catches a specification that says the wrong thing, which no proof can. |

        ## Checking it

        - **Build** (`lake build`) checks every proof with Lean.
        - **Tenet ▸ Verify Project** checks them again with a second, independent kernel.
        - **Tenet ▸ Assurance Report** says what can be relied on: what is proved outright, anything resting on `sorry` or an axiom, any proof that trusts compiled code, and any definition no theorem talks about. This project starts at 100%; keep it there.
        - **Tenet ▸ What's Proved About This?** on any definition lists the theorems about it, read in plain English. Check that they say what you mean.

        The workflow in `.github/workflows/lean_assurance.yml` runs the same assurance check on every push and pull request, and fails on `sorry`.

        ## Making it yours

        Change the specification first, then the implementation, then repair the proofs. A proof that still goes through after you change the specification is worth a second look: it may not have been saying what you thought.

        """;

    // ---- crypto: modular exponentiation ----

    private const string CryptoReadme = """
        # {{Package}}

        Modular exponentiation, `b ^ e mod m`, the operation underneath RSA and Diffie–Hellman, implemented with
        square-and-multiply and **proved** to compute exactly `b ^ e % m` for every base, exponent and modulus.

        """;

    private const string CryptoSpec = """
        /-!
        # The specification

        `b ^ e % m`, exactly as a textbook writes it. Obviously right, and hopelessly slow for the numbers
        cryptography uses: `b ^ e` has billions of digits before the `% m` is taken.
        -/

        namespace {{Lib}}

        /-- `b` to the power `e`, modulo `m`. -/
        def powModSpec (b e m : Nat) : Nat := b ^ e % m

        end {{Lib}}

        """;

    private const string CryptoImpl = """
        /-!
        # The implementation

        Square-and-multiply, the way RSA and Diffie–Hellman really compute it: about `log₂ e` steps, and every
        intermediate value stays below `m * m`.
        -/

        namespace {{Lib}}

        /-- `b ^ e % m` by repeated squaring: `b ^ e = (b ^ (e / 2)) ^ 2`, times `b` once more when `e` is odd. -/
        def powMod (b e m : Nat) : Nat :=
          if e = 0 then 1 % m
          else
            let h := powMod b (e / 2) m
            let sq := h * h % m
            if e % 2 = 0 then sq else sq * (b % m) % m
        termination_by e
        decreasing_by omega

        end {{Lib}}

        """;

    private const string CryptoProofs = """
        import {{Lib}}.Spec
        import {{Lib}}.Impl

        /-!
        # Why they agree
        -/

        namespace {{Lib}}

        /-- The implementation computes exactly the specification, for every base, exponent and modulus. -/
        theorem powMod_eq_spec (b e m : Nat) : powMod b e m = powModSpec b e m := by
          induction e using Nat.strongRecOn with
          | _ e ih =>
            unfold powModSpec
            rw [powMod]
            split
            · simp [*]
            · have h := ih (e / 2) (by omega)
              unfold powModSpec at h
              dsimp only
              rw [h]
              split
              · rw [← Nat.mul_mod, ← Nat.pow_add]
                congr 2
                omega
              · rw [← Nat.mul_mod (b ^ (e / 2)) (b ^ (e / 2)) m, ← Nat.mul_mod (b ^ (e / 2) * b ^ (e / 2)) b m,
                  ← Nat.pow_add, ← Nat.pow_succ]
                congr 2
                omega

        /-- Diffie–Hellman works: Alice raising Bob's public value to her secret gets the same number as Bob raising
        Alice's to his. -/
        theorem diffieHellman_agree (g a b p : Nat) :
            powMod (powMod g a p) b p = powMod (powMod g b p) a p := by
          simp only [powMod_eq_spec, powModSpec]
          rw [← Nat.pow_mod, ← Nat.pow_mod, ← Nat.pow_mul, ← Nat.pow_mul, Nat.mul_comm]

        end {{Lib}}

        """;

    private const string CryptoTests = """
        import {{Lib}}.Impl
        import {{Lib}}.Spec

        /-!
        # Known answers

        The standard textbook RSA example: p = 61, q = 53, n = 3233, e = 17, d = 2753. Encrypting 65 gives 2790, and
        decrypting 2790 gives 65 back.
        -/

        namespace {{Lib}}

        #guard powMod 65 17 3233 == 2790
        #guard powMod 2790 2753 3233 == 65

        -- The implementation agrees with the specification on small inputs, as the proof says it must.
        #guard (List.range 40).all fun e => powMod 7 e 13 == powModSpec 7 e 13

        -- And it is fast where the specification is not: a 128-bit exponent, instantly.
        #guard powMod 2 (2 ^ 128) 1000000007 < 1000000007

        end {{Lib}}

        """;

    // ---- parser: prefix expressions with a proved round trip ----

    private const string ParserReadme = """
        # {{Package}}

        A parser for arithmetic expressions written in prefix (Polish) notation, `+ 1 * 2 3` for `1 + 2 * 3`, with
        its printer, and a **proof** that parsing what was printed always gives the expression back.

        """;

    private const string ParserSpec = """
        /-!
        # The specification

        What an expression is, what its tokens are, and how it is written out. The printer *is* the definition of
        the format: the parser is right exactly when it undoes it.
        -/

        namespace {{Lib}}

        /-- What is parsed: arithmetic on natural numbers. -/
        inductive Expr where
          | num (n : Nat)
          | add (a b : Expr)
          | mul (a b : Expr)
        deriving Repr, DecidableEq

        /-- What the parser reads: tokens in prefix order. -/
        inductive Token where
          | num (n : Nat)
          | plus
          | times
        deriving Repr, DecidableEq

        /-- Write an expression out as tokens, operator first. -/
        def print : Expr → List Token
          | .num n => [.num n]
          | .add a b => .plus :: print a ++ print b
          | .mul a b => .times :: print a ++ print b

        /-- How many tokens an expression takes. -/
        def size : Expr → Nat
          | .num _ => 1
          | .add a b => size a + size b + 1
          | .mul a b => size a + size b + 1

        end {{Lib}}

        """;

    private const string ParserImpl = """
        import {{Lib}}.Spec

        /-!
        # The implementation

        A recursive-descent parser. `fuel` bounds how deep it goes, which makes it obviously terminating; the
        number of tokens is always enough.
        -/

        namespace {{Lib}}

        /-- Read one expression off the front of the tokens, and give back the tokens after it. -/
        def parseWith : Nat → List Token → Option (Expr × List Token)
          | 0, _ => none
          | _ + 1, [] => none
          | _ + 1, .num n :: rest => some (.num n, rest)
          | fuel + 1, .plus :: rest => do
            let (a, rest) ← parseWith fuel rest
            let (b, rest) ← parseWith fuel rest
            pure (.add a b, rest)
          | fuel + 1, .times :: rest => do
            let (a, rest) ← parseWith fuel rest
            let (b, rest) ← parseWith fuel rest
            pure (.mul a b, rest)

        /-- Parse a whole list of tokens as one expression; `none` unless every token is used. -/
        def parse (ts : List Token) : Option Expr :=
          match parseWith ts.length ts with
          | some (e, []) => some e
          | _ => none

        end {{Lib}}

        """;

    private const string ParserProofs = """
        import {{Lib}}.Spec
        import {{Lib}}.Impl

        /-!
        # Why they agree
        -/

        namespace {{Lib}}

        theorem length_print (e : Expr) : (print e).length = size e := by
          induction e <;> simp [print, size, *] <;> omega

        /-- Parsing what was printed, with any tokens after it, gives back the expression and those tokens. -/
        theorem parseWith_print (e : Expr) (rest : List Token) (fuel : Nat) (h : size e ≤ fuel) :
            parseWith fuel (print e ++ rest) = some (e, rest) := by
          induction e generalizing rest fuel with
          | num n =>
            cases fuel with
            | zero => simp [size] at h
            | succ f => simp [print, parseWith]
          | add a b iha ihb =>
            cases fuel with
            | zero => simp [size] at h
            | succ f =>
              simp only [size] at h
              simp [print, parseWith, List.append_assoc, iha _ f (by omega), ihb _ f (by omega)]
          | mul a b iha ihb =>
            cases fuel with
            | zero => simp [size] at h
            | succ f =>
              simp only [size] at h
              simp [print, parseWith, List.append_assoc, iha _ f (by omega), ihb _ f (by omega)]

        /-- The round trip: parsing a printed expression gives the expression back. -/
        theorem parse_print (e : Expr) : parse (print e) = some e := by
          have := parseWith_print e [] (print e).length (by rw [length_print]; exact Nat.le_refl _)
          simp only [List.append_nil] at this
          simp [parse, this]

        end {{Lib}}

        """;

    private const string ParserTests = """
        import {{Lib}}.Impl

        /-!
        # Known answers
        -/

        namespace {{Lib}}

        -- `+ 1 * 2 3` is `1 + 2 * 3`.
        #guard parse [.plus, .num 1, .times, .num 2, .num 3] == some (.add (.num 1) (.mul (.num 2) (.num 3)))
        -- An operator with one operand is not an expression,
        #guard parse [.plus, .num 1] == none
        -- and neither are two numbers side by side.
        #guard parse [.num 1, .num 2] == none

        end {{Lib}}

        """;

    // ---- file format: tag-length-value records ----

    private const string FormatReadme = """
        # {{Package}}

        A binary file format of tag-length-value records, the shape of TLS, ASN.1 BER and many others, with its
        encoder and decoder, and a **proof** that decoding an encoded file always gives back exactly its records.

        """;

    private const string FormatSpec = """
        /-!
        # The specification

        What a record is, and how records are written. The encoder *is* the definition of the format: the decoder
        is right exactly when it undoes it.
        -/

        namespace {{Lib}}

        /-- One record: a tag byte and up to 255 bytes of payload. -/
        structure Record where
          tag : UInt8
          payload : List UInt8
          short : payload.length < 256
        deriving Repr

        /-- A record on disk: its tag, the payload's length as one byte, then the payload. -/
        def encode (r : Record) : List UInt8 :=
          r.tag :: r.payload.length.toUInt8 :: r.payload

        /-- A file: its records, one after another. -/
        def encodeAll (rs : List Record) : List UInt8 := (rs.map encode).flatten

        end {{Lib}}

        """;

    private const string FormatImpl = """
        import {{Lib}}.Spec

        /-!
        # The implementation

        The decoder, which has to cope with any bytes at all: short files, lengths that run past the end.
        -/

        namespace {{Lib}}

        /-- Read one record off the front of the bytes, and give back the bytes after it. -/
        def decode : List UInt8 → Option (Record × List UInt8)
          | tag :: len :: rest =>
            if len.toNat ≤ rest.length then
              some (⟨tag, rest.take len.toNat, by rw [List.length_take]; have := UInt8.toNat_lt len; omega⟩,
                rest.drop len.toNat)
            else none
          | _ => none

        /-- Read up to `fuel` records until the bytes run out; `none` if they end in the middle of one. -/
        def decodeAllWith : Nat → List UInt8 → Option (List Record)
          | _, [] => some []
          | 0, _ :: _ => none
          | fuel + 1, b :: bs =>
            match decode (b :: bs) with
            | none => none
            | some (r, rest) => (decodeAllWith fuel rest).map (r :: ·)

        /-- Read a whole file. Every record takes at least two bytes, so there are never more records than bytes. -/
        def decodeAll (bytes : List UInt8) : Option (List Record) := decodeAllWith bytes.length bytes

        end {{Lib}}

        """;

    private const string FormatProofs = """
        import {{Lib}}.Spec
        import {{Lib}}.Impl

        /-!
        # Why they agree
        -/

        namespace {{Lib}}

        /-- Decoding an encoded record, with any bytes after it, gives back the record and those bytes. -/
        theorem decode_encode (r : Record) (rest : List UInt8) : decode (encode r ++ rest) = some (r, rest) := by
          obtain ⟨tag, payload, short⟩ := r
          have hlen : payload.length.toUInt8.toNat = payload.length := by
            simp [Nat.toUInt8]; omega
          simp [encode, decode, hlen]

        theorem length_encodeAll (rs : List Record) : rs.length ≤ (encodeAll rs).length := by
          induction rs with
          | nil => simp
          | cons r rs ih => simp [encodeAll, encode] at ih ⊢; omega

        theorem decodeAllWith_encodeAll (rs : List Record) (fuel : Nat) (h : rs.length ≤ fuel) :
            decodeAllWith fuel (encodeAll rs) = some rs := by
          induction rs generalizing fuel with
          | nil => cases fuel <;> simp [encodeAll, decodeAllWith]
          | cons r rs ih =>
            cases fuel with
            | zero => simp at h
            | succ f =>
              have e : encodeAll (r :: rs) = r.tag :: (r.payload.length.toUInt8 :: (r.payload ++ encodeAll rs)) := by
                simp [encodeAll, encode]
              have d := decode_encode r (encodeAll rs)
              simp only [encode, List.cons_append] at d
              rw [e, decodeAllWith, d]
              simp [ih f (by simp at h; omega)]

        /-- The round trip: reading a file gives back exactly the records it was written from. -/
        theorem decodeAll_encodeAll (rs : List Record) : decodeAll (encodeAll rs) = some rs :=
          decodeAllWith_encodeAll rs _ (length_encodeAll rs)

        end {{Lib}}

        """;

    private const string FormatTests = """
        import {{Lib}}.Impl

        /-!
        # Known answers
        -/

        namespace {{Lib}}

        -- Tag 7, two bytes of payload, and one byte left over for the next record.
        #guard (decode [7, 2, 0xAB, 0xCD, 99]).map (fun (r, rest) => (r.tag, r.payload, rest)) == some (7, [0xAB, 0xCD], [99])
        -- A length that runs past the end of the file is refused, not read past.
        #guard (decode [7, 3, 0xAB]).isNone
        -- A file that ends in the middle of a record is refused.
        #guard (decodeAll [7, 1, 0xAB, 8]).isNone

        end {{Lib}}

        """;
}
