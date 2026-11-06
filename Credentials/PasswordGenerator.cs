using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace AmpzDesktopBooster.Credentials;

/// <summary>
/// Generador de contraseñas del editor de credenciales. Puro (sin UI ni estado) para que la regla
/// viva en un solo lugar.
///
/// Usa <see cref="RandomNumberGenerator"/> y NUNCA <see cref="Random"/>: <c>Random</c> es un PRNG
/// pensado para simulaciones, predecible si se conoce su estado — una contraseña sacada de ahí es
/// adivinable. <see cref="RandomNumberGenerator.GetInt32(int)"/> además es uniforme (sin el sesgo
/// del clásico <c>byte % n</c>).
/// </summary>
public static class PasswordGenerator
{
    public const int MinLength = 8;
    public const int MaxLength = 64;
    public const int DefaultLength = 20;

    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    // Símbolos que aceptan casi todos los formularios. Se dejan afuera comillas, backslash, espacio y
    // backtick: son los que rompen al pegar la contraseña en un .env, un JSON o una línea de comandos.
    private const string Symbols = "!@#$%^&*()-_=+[]{};:,.?/~";

    /// <summary>
    /// Genera una contraseña de <paramref name="length"/> caracteres con los conjuntos pedidos,
    /// garantizando AL MENOS UNO de cada conjunto prendido (muchos sitios rechazan una contraseña "sin
    /// número" aunque haya salido así por azar). null si el pedido no es válido (largo fuera de rango
    /// o ningún conjunto): el caller dice qué falta, acá no se adivina.
    /// </summary>
    public static string? Generate(int length, bool upper, bool lower, bool digits, bool symbols)
    {
        if (length < MinLength || length > MaxLength) return null;

        var sets = new List<string>();
        if (upper) sets.Add(Upper);
        if (lower) sets.Add(Lower);
        if (digits) sets.Add(Digits);
        if (symbols) sets.Add(Symbols);
        if (sets.Count == 0) return null;

        string pool = string.Concat(sets);
        var chars = new char[length];
        int i = 0;
        // Uno garantizado de cada conjunto (sets.Count ≤ 4 ≤ MinLength, así que siempre entran)…
        foreach (var set in sets) chars[i++] = set[RandomNumberGenerator.GetInt32(set.Length)];
        // …y el resto del conjunto unido.
        for (; i < length; i++) chars[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];

        // Fisher–Yates con el mismo RNG: sin esto los garantizados quedarían siempre AL PRINCIPIO
        // (una mayúscula primero, un dígito tercero…), un patrón que achica el espacio a adivinar.
        for (int j = length - 1; j > 0; j--)
        {
            int k = RandomNumberGenerator.GetInt32(j + 1);
            (chars[j], chars[k]) = (chars[k], chars[j]);
        }

        string result = new(chars);
        Array.Clear(chars);
        return result;
    }
}
