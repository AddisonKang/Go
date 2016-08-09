﻿using Newtonsoft.Json;
using PokemonGo.RocketAPI.Exceptions;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace PokemonGo.RocketAPI.Login
{
    class PtcLogin : ILoginType
    {
        readonly string password;
        readonly string username;
        readonly ISettings settings;

        public PtcLogin(string username, string password, ISettings settings)
        {
            this.username = username;
            this.password = password;
            this.settings = settings;
        }
        public async Task<string> GetAccessToken()
        {
            ProxyEx proxy = new ProxyEx
            {
                Address = settings.ProxyIP,
                Port = settings.ProxyPort,
                Username = settings.ProxyUsername,
                Password = settings.ProxyPassword
            };

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip,
                AllowAutoRedirect = false,
                Proxy = proxy.AsWebProxy(),
                UseProxy = true
            };

            using (var tempHttpClient = new System.Net.Http.HttpClient(handler))
            {
                tempHttpClient.Timeout = TimeSpan.FromSeconds(10);

                //Get session cookie
                var sessionData = await GetSessionCookie(tempHttpClient).ConfigureAwait(false);

                //Login
                var ticketId = await GetLoginTicket(username, password, tempHttpClient, sessionData).ConfigureAwait(false);

                //Get tokenvar
                return await GetToken(tempHttpClient, ticketId).ConfigureAwait(false);
            }
        }

        private async static Task<string> ExtracktTicketFromResponse(HttpResponseMessage loginResp)
        {
            var location = loginResp.Headers.Location;
            var contentResponse = await loginResp.Content.ReadAsStringAsync();

            if (!String.IsNullOrEmpty(contentResponse))
            {
                dynamic responseObject = JsonConvert.DeserializeObject<dynamic>(contentResponse.Trim());

                if(responseObject["errors"] != null)
                {
                    foreach(dynamic error in responseObject["errors"])
                    {
                        if(error.Value.Contains("Your username or password is incorrect"))
                        {
                            throw new InvalidCredentialsException(error.Value);
                        }
                        else if (error.Value.Contains("As a security measure, your account has been disabled"))
                        {
                            throw new InvalidCredentialsException(error.Value);
                        }
                    }
                }
            }

            if (location == null)
                throw new LoginFailedException();

            var ticketId = HttpUtility.ParseQueryString(location.Query)["ticket"];

            if (ticketId == null)
                throw new PtcOfflineException();

            return ticketId;
        }

        private static IDictionary<string, string> GenerateLoginRequest(SessionData sessionData, string user, string pass)
        {
            return new Dictionary<string, string>
            {
                { "lt", sessionData.Lt },
                { "execution", sessionData.Execution },
                { "_eventId", "submit" },
                { "username", user },
                { "password", pass }
            };
        }

        private static IDictionary<string, string> GenerateTokenVarRequest(string ticketId)
        {
            return new Dictionary<string, string>
            {
                {"client_id", "mobile-app_pokemon-go"},
                {"redirect_uri", "https://www.nianticlabs.com/pokemongo/error"},
                {"client_secret", "w8ScCUXJQc6kXKw8FiOhd8Fixzht18Dq3PEVkUCP5ZPxtgyWsbTvWHFLm2wNY0JR"},
                {"grant_type", "refresh_token"},
                {"code", ticketId}
            };
        }

        private static async Task<string> GetLoginTicket(string username, string password, System.Net.Http.HttpClient tempHttpClient, SessionData sessionData)
        {
            HttpResponseMessage loginResp;
            var loginRequest = GenerateLoginRequest(sessionData, username, password);
            using (var formUrlEncodedContent = new FormUrlEncodedContent(loginRequest))
            {
                loginResp = await tempHttpClient.PostAsync(Resources.PtcLoginUrl, formUrlEncodedContent).ConfigureAwait(false);
            }

            var ticketId = await ExtracktTicketFromResponse(loginResp);
            return ticketId;
        }

        private static async Task<SessionData> GetSessionCookie(System.Net.Http.HttpClient tempHttpClient)
        {
            var sessionResp = await tempHttpClient.GetAsync(Resources.PtcLoginUrl).ConfigureAwait(false);
            var data = await sessionResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var sessionData = JsonConvert.DeserializeObject<SessionData>(data);
            return sessionData;
        }

        private static async Task<string> GetToken(System.Net.Http.HttpClient tempHttpClient, string ticketId)
        {
            HttpResponseMessage tokenResp;
            var tokenRequest = GenerateTokenVarRequest(ticketId);
            using (var formUrlEncodedContent = new FormUrlEncodedContent(tokenRequest))
            {
                tokenResp = await tempHttpClient.PostAsync(Resources.PtcLoginOauth, formUrlEncodedContent).ConfigureAwait(false);
            }

            var tokenData = await tokenResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return HttpUtility.ParseQueryString(tokenData)["access_token"];
        }

        private class SessionData
        {
            public string Lt { get; set; }
            public string Execution { get; set; }
        }
    }
}