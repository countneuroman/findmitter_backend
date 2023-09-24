﻿using Api.Configs;
using Api.Consts;
using Api.Models.Token;
using Common;
using DAL;
using DAL.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Api.Exceptions;

namespace Api.Services
{
    public class AuthService
    {
        private readonly DataContext _context;
        private readonly AuthConfig _config;

        public AuthService(IOptions<AuthConfig> config, DataContext context)
        {
            _context = context;
            _config = config.Value;
        }

        public async Task<TokenModel> GetToken(string login, string password)
        {
            var user = await GetUserByCredention(login, password);
            var session = await _context.UserSessions.AddAsync(new UserSession
            {
                User = user,
                RefreshToken = Guid.NewGuid(),
                Created = DateTime.UtcNow,
                Id = Guid.NewGuid()
            });
            await _context.SaveChangesAsync();
            return GenerateTokens(session.Entity);
        }
        public async Task<TokenModel> GetTokenByRefreshToken(string refreshToken)
        {
            var validParams = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                IssuerSigningKey = _config.SymmetricSecurityKey()
            };
            var principal = new JwtSecurityTokenHandler().ValidateToken(refreshToken, validParams, out var securityToken);

            if (securityToken is not JwtSecurityToken jwtToken
                || !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256,
                StringComparison.InvariantCultureIgnoreCase))
            {
                throw new SecurityTokenException("invalid token");
            }

            if (principal.Claims.FirstOrDefault(x => x.Type == "refreshToken")?.Value is string refreshIdString
                && Guid.TryParse(refreshIdString, out var refreshId)
                )
            {
                var session = await GetSessionByRefreshToken(refreshId);
                if (!session.IsActive)
                    throw new Exception("session is not active");
                
                session.RefreshToken = Guid.NewGuid();
                await _context.SaveChangesAsync();

                return GenerateTokens(session);
            }
            else
                throw new SecurityTokenException("invalid token");
        }
        public async Task<UserSession> GetSessionById(Guid id)
        {
            var session = await _context.UserSessions.FirstOrDefaultAsync(x => x.Id == id);
            if (session == null)
                throw new Exception("session is not found");
            
            return session;
        }
        
        private async Task<User> GetUserByCredention(string login, string pass)
        {
            var user = await _context.Users.FirstOrDefaultAsync(x => x.Name.ToLower() == login.ToLower());
            if (user == null)
                throw new UserNotFoundException();

            if (!HashHelper.Verify(pass, user.PasswordHash))
                throw new Exception("password is incorrect");

            return user;
        }
        private TokenModel GenerateTokens(UserSession session)
        {
            var dtNow = DateTime.Now;
            if (session.User == null)
                throw new UserNotFoundException();

            var jwt = new JwtSecurityToken(
                issuer: _config.Issuer,
                audience: _config.Audience,
                notBefore: dtNow,
                claims: new Claim[] {
            new Claim(ClaimsIdentity.DefaultNameClaimType, session.User.Name),
            new Claim(ClaimNames.SessionId, session.Id.ToString()),
            new Claim(ClaimNames.Id, session.User.Id.ToString()),
            },
                expires: DateTime.Now.AddMinutes(_config.LifeTime),
                signingCredentials: new SigningCredentials(_config.SymmetricSecurityKey(), SecurityAlgorithms.HmacSha256)
                );
            var encodedJwt = new JwtSecurityTokenHandler().WriteToken(jwt);

            var refresh = new JwtSecurityToken(
                notBefore: dtNow,
                claims: new Claim[] {
                new Claim(ClaimNames.RefreshToken, session.RefreshToken.ToString()),
                },
                expires: DateTime.Now.AddHours(_config.LifeTime),
                signingCredentials: new SigningCredentials(_config.SymmetricSecurityKey(), SecurityAlgorithms.HmacSha256)
                );
            var encodedRefresh = new JwtSecurityTokenHandler().WriteToken(refresh);

            return new TokenModel(encodedJwt, encodedRefresh);

        }
        private async Task<UserSession> GetSessionByRefreshToken(Guid refreshTokenId)
        {
            var session = await _context.UserSessions.Include(x => x.User)
                .FirstOrDefaultAsync(x => x.RefreshToken == refreshTokenId);
            if (session == null)
                throw new Exception("session is not found");
            return session;
        }

    }
}
