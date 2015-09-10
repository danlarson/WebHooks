﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http.Controllers;
using Microsoft.AspNet.WebHooks.Properties;
using Newtonsoft.Json.Linq;
using PayPal.Api;

namespace Microsoft.AspNet.WebHooks
{
    /// <summary>
    /// Provides an <see cref="IWebHookReceiver"/> implementation which supports WebHooks generated by Paypal using the
    /// Paypal .NET SDK, see <c>https://www.nuget.org/packages/PayPal</c>.
    /// Configure the Paypal WebHook settings using the web.config file as described in <c>https://github.com/paypal/PayPal-NET-SDK/wiki/Webhook-Event-Validation</c>. 
    /// The corresponding WebHook URI is of the form '<c>https://&lt;host&gt;/api/webhooks/incoming/paypal</c>'.
    /// For details about Paypal WebHooks, see <c>https://developer.paypal.com/webapps/developer/docs/integration/direct/rest-webhooks-overview/</c>.
    /// </summary>
    public class PaypalWebHookReceiver : WebHookReceiver
    {
        internal const string ReceiverName = "mailchimp";
        internal const string EventTypeParameter = "event_type";

        private readonly object _thisLock = new object();
        private readonly OAuthTokenCredential _credentials;
        private readonly Dictionary<string, string> _config;

        /// <summary>
        /// Initializes a new instance of the <see cref="PaypalWebHookReceiver"/> class.
        /// </summary>
        public PaypalWebHookReceiver()
            : this(initialize: true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PaypalWebHookReceiver"/> class.
        /// This constructor is intended for testing purposes.
        /// </summary>
        internal PaypalWebHookReceiver(bool initialize)
        {
            if (initialize)
            {
                try
                {
                    _credentials = new OAuthTokenCredential();
                    _config = ConfigManager.GetConfigWithDefaults(ConfigManager.Instance.GetProperties());
                }
                catch (Exception ex)
                {
                    string msg = string.Format(CultureInfo.CurrentCulture, PaypalReceiverResources.Receiver_InitFailure, ex.Message);
                    throw new InvalidOperationException(msg, ex);
                }
            }
        }

        /// <inheritdoc />
        public override string Name
        {
            get { return ReceiverName; }
        }

        /// <inheritdoc />
        public override async Task<HttpResponseMessage> ReceiveAsync(string id, HttpRequestContext context, HttpRequestMessage request)
        {
            if (id == null)
            {
                throw new ArgumentNullException("id");
            }
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (request.Method == HttpMethod.Post)
            {
                NameValueCollection requestHeaders = GetRequestHeaders(request);
                string requestBody = request.Content != null ? await request.Content.ReadAsStringAsync() : string.Empty;

                // Perform WebHook validation
                var isValid = ValidateReceivedEvent(context, requestHeaders, requestBody);
                if (!isValid)
                {
                    HttpResponseMessage badHook = request.CreateErrorResponse(HttpStatusCode.BadRequest, PaypalReceiverResources.Receiver_InvalidWebHook);
                    return badHook;
                }

                // Read the request entity body
                JObject data = await ReadAsJsonAsync(request);

                string action = data.Value<string>(EventTypeParameter);
                if (string.IsNullOrEmpty(action))
                {
                    string msg = string.Format(CultureInfo.CurrentCulture, PaypalReceiverResources.Receiver_BadBody, EventTypeParameter);
                    context.Configuration.DependencyResolver.GetLogger().Error(msg);
                    HttpResponseMessage badType = request.CreateErrorResponse(HttpStatusCode.BadRequest, msg);
                    return badType;
                }

                // Call registered handlers
                return await ExecuteWebHookAsync(id, context, request, new string[] { action }, data);
            }
            else
            {
                return CreateBadMethodResponse(request);
            }
        }

        internal static NameValueCollection GetRequestHeaders(HttpRequestMessage request)
        {
            NameValueCollection requestHeaders = new NameValueCollection();
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                foreach (var value in header.Value)
                {
                    requestHeaders.Add(header.Key, value);
                }
            }
            return requestHeaders;
        }

        /// <summary>
        /// Validates the received WebHook.
        /// </summary>
        /// <param name="context">The current <see cref="HttpRequestContext"/>.</param>
        /// <param name="headers">The request headers for the current <see cref="HttpRequestMessage"/>.</param>
        /// <param name="body">The request body for the current <see cref="HttpRequestMessage"/>.</param>
        /// <returns><c>true</c> if received WebHook is valid; <c>false</c> otherwise.</returns>
        protected virtual bool ValidateReceivedEvent(HttpRequestContext context, NameValueCollection headers, string body)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            // Get existing or new access token. We put a lock around it as it is not thread safe otherwise.
            string accessToken;
            lock (_thisLock)
            {
                accessToken = _credentials.GetAccessToken();
            }
            APIContext apiContext = new APIContext(accessToken);
            apiContext.Config = _config;

            try
            {
                bool isValid = WebhookEvent.ValidateReceivedEvent(apiContext, headers, body);
                if (!isValid)
                {
                    context.Configuration.DependencyResolver.GetLogger().Error(PaypalReceiverResources.Receiver_InvalidWebHook);
                }
                return isValid;
            }
            catch (Exception ex)
            {
                string msg = string.Format(CultureInfo.CurrentCulture, PaypalReceiverResources.Receiver_ValidationFailure, ex.Message);
                context.Configuration.DependencyResolver.GetLogger().Error(msg, ex);
            }
            return false;
        }
    }
}
