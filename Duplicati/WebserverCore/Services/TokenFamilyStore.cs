// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
using Duplicati.Library.Main.Database;
using Duplicati.Server.Database;
using Duplicati.WebserverCore.Abstractions;

namespace Duplicati.WebserverCore.Services;

public class TokenFamilyStore(Connection connection) : ITokenFamilyStore
{
    // Use Dapper?
    public Task<ITokenFamilyStore.TokenFamily> CreateTokenFamily(string userId, CancellationToken ct)
    {
        var familyId = System.Security.Cryptography.RandomNumberGenerator.GetHexString(16);
        var counter = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1024) % 1024;
        var lastUpdated = DateTime.UtcNow;
        connection.ExecuteWithCommand(cmd =>
        {
            cmd.SetCommandAndParameters(@"INSERT INTO TokenFamily (""Id"", ""UserId"", ""Counter"", ""LastUpdated"") VALUES (@Id, @UserId, @Counter, @LastUpdated)")
                .SetParameterValue("@Id", familyId)
                .SetParameterValue("@UserId", userId)
                .SetParameterValue("@Counter", counter)
                .SetParameterValue("@LastUpdated", lastUpdated.Ticks)
                .ExecuteNonQuery();
        });

        return Task.FromResult(new ITokenFamilyStore.TokenFamily(familyId, userId, counter, lastUpdated));
    }

    public Task<ITokenFamilyStore.TokenFamily> GetTokenFamily(string userId, string familyId, CancellationToken ct)
    {
        ITokenFamilyStore.TokenFamily? family = null;
        connection.ExecuteWithCommand(cmd =>
        {
            cmd.SetCommandAndParameters(@"SELECT ""Id"", ""UserId"", ""Counter"", ""LastUpdated"" FROM ""TokenFamily"" WHERE ""Id"" = @Id AND ""UserId"" = @UserId")
                .SetParameterValue("@Id", familyId)
                .SetParameterValue("@UserId", userId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return;

            family = new ITokenFamilyStore.TokenFamily(
                reader.ConvertValueToString(0) ?? throw new Exception("Token family ID is null"),
                reader.ConvertValueToString(1) ?? throw new Exception("Token family user ID is null"),
                reader.GetInt32(2),
                new DateTime(reader.ConvertValueToInt64(3))
            );
        });
        return Task.FromResult(family ?? throw new Exceptions.UnauthorizedException("Token family not found"));
    }

    public Task<ITokenFamilyStore.TokenFamily> IncrementTokenFamily(ITokenFamilyStore.TokenFamily tokenFamily, CancellationToken ct)
    {
        var nextCounter = tokenFamily.Counter + 1;
        var lastUpdated = DateTime.UtcNow;
        connection.ExecuteWithCommand(cmd =>
        {
            cmd.SetCommandAndParameters(@"UPDATE ""TokenFamily"" SET ""Counter"" = @NextCounter, ""LastUpdated"" = @LastUpdated WHERE ""Id"" = @Id AND ""UserId"" = @UserId AND ""Counter"" = @PrevCounter")
                .SetParameterValue("@NextCounter", nextCounter)
                .SetParameterValue("@LastUpdated", lastUpdated.Ticks)
                .SetParameterValue("@Id", tokenFamily.Id)
                .SetParameterValue("@UserId", tokenFamily.UserId)
                .SetParameterValue("@PrevCounter", tokenFamily.Counter);
            if (cmd.ExecuteNonQuery() != 1)
                throw new Exceptions.ConflictException("Token family counter mismatch or not found");
        });

        return Task.FromResult(new ITokenFamilyStore.TokenFamily(tokenFamily.Id, tokenFamily.UserId, nextCounter, lastUpdated));
    }

    public Task InvalidateTokenFamily(string userId, string familyId, CancellationToken ct)
    {
        connection.ExecuteWithCommand(cmd =>
        {
            cmd.SetCommandAndParameters(@"DELETE FROM ""TokenFamily"" WHERE ""Id"" = @Id AND ""UserId"" = @UserId")
                .SetParameterValue("@Id", familyId)
                .SetParameterValue("@UserId", userId);

            if (cmd.ExecuteNonQuery() != 1)
                throw new Exceptions.NotFoundException("Token family not found");
        });

        return Task.CompletedTask;
    }

    public Task InvalidateAllTokenFamilies(string userId, CancellationToken ct)
    {
        connection.ExecuteWithCommand(cmd =>
        {
            cmd.SetCommandAndParameters(@"DELETE FROM ""TokenFamily"" WHERE ""UserId"" = @UserId")
                .SetParameterValue("@UserId", userId)
                .ExecuteNonQuery();
        });

        return Task.CompletedTask;
    }

    public Task InvalidateAllTokens(CancellationToken ct)
    {
        connection.ExecuteWithCommand(cmd =>
            cmd.ExecuteNonQuery(@"DELETE FROM ""TokenFamily""")
        );

        return Task.CompletedTask;
    }

}
