﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Relay
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// The SharedAccessSignatureTokenProvider generates tokens using a shared access key or existing signature.
    /// </summary>
    class SharedAccessSignatureTokenProvider : TokenProvider
    {
        public static readonly DateTime EpochTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        readonly byte[] encodedSharedAccessKey;
        readonly string keyName;
        readonly string sharedAccessSignature;

        internal SharedAccessSignatureTokenProvider(string sharedAccessSignature)
        {
            SharedAccessSignatureToken.Validate(sharedAccessSignature);
            this.sharedAccessSignature = sharedAccessSignature;
        }

        internal SharedAccessSignatureTokenProvider(string keyName, string sharedAccessKey)
            : this(keyName, sharedAccessKey, MessagingTokenProviderKeyEncoder)
        {
        }

        protected SharedAccessSignatureTokenProvider(string keyName, string sharedAccessKey, Func<string, byte[]> customKeyEncoder)
        {
            if (string.IsNullOrEmpty(keyName) || string.IsNullOrEmpty(sharedAccessKey))
            {
                throw RelayEventSource.Log.ArgumentNull(string.IsNullOrEmpty(keyName) ? nameof(keyName) : nameof(sharedAccessKey));
            }

            if (keyName.Length > SharedAccessSignatureToken.MaxKeyNameLength)
            {
                throw RelayEventSource.Log.ArgumentOutOfRange(
                    nameof(keyName),
                    keyName.Substring(0, 10) + "...",
                    SR.GetString(SR.ArgumentStringTooBig, nameof(keyName), SharedAccessSignatureToken.MaxKeyNameLength));
            }

            if (sharedAccessKey.Length > SharedAccessSignatureToken.MaxKeyLength)
            {
                throw RelayEventSource.Log.ArgumentOutOfRange(
                    nameof(sharedAccessKey),
                    sharedAccessKey.Substring(0, 10) + "...",
                    SR.GetString(SR.ArgumentStringTooBig, nameof(sharedAccessKey), SharedAccessSignatureToken.MaxKeyLength));
            }

            this.keyName = keyName;
            this.encodedSharedAccessKey = customKeyEncoder != null ?
                customKeyEncoder(sharedAccessKey) :
                MessagingTokenProviderKeyEncoder(sharedAccessKey);
        }

        protected override Task<SecurityToken> OnGetTokenAsync(string resource, TimeSpan validFor)
        {
            string tokenString = this.BuildSignature(resource, validFor);
            var securityToken = new SharedAccessSignatureToken(tokenString);
            return Task.FromResult<SecurityToken>(securityToken);
        }

        protected virtual string BuildSignature(string resource, TimeSpan validFor)
        {
            if (string.IsNullOrWhiteSpace(this.sharedAccessSignature))
            {
                return SharedAccessSignatureBuilder.BuildSignature(this.keyName, this.encodedSharedAccessKey, resource, validFor);
            }

            return this.sharedAccessSignature;
        }

        static class SharedAccessSignatureBuilder
        {
            [SuppressMessage("Microsoft.Globalization", "CA1308:NormalizeStringsToUppercase", Justification = "Uris are normalized to lowercase")]
            public static string BuildSignature(
                string keyName,
                byte[] encodedSharedAccessKey,
                string resource,
                TimeSpan timeToLive)
            {
                // Note that target URI is not normalized because in IoT scenario it 
                // is case sensitive.
                string expiresOn = BuildExpiresOn(timeToLive);
                string audienceUri = WebUtility.UrlEncode(resource);
                List<string> fields = new List<string> { audienceUri, expiresOn };

                // Example string to be signed:
                // http://mynamespace.servicebus.windows.net/a/b/c?myvalue1=a
                // <Value for ExpiresOn>
                string signature = Sign(string.Join("\n", fields), encodedSharedAccessKey);

                // Example returned string:
                // SharedAccessKeySignature
                // sr=ENCODED(http://mynamespace.servicebus.windows.net/a/b/c?myvalue1=a)&sig=<Signature>&se=<ExpiresOnValue>&skn=<KeyName>

                return string.Format(CultureInfo.InvariantCulture, "{0} {1}={2}&{3}={4}&{5}={6}&{7}={8}",
                    SharedAccessSignatureToken.SharedAccessSignature,
                    SharedAccessSignatureToken.SignedResource, audienceUri,
                    SharedAccessSignatureToken.Signature, WebUtility.UrlEncode(signature),
                    SharedAccessSignatureToken.SignedExpiry, WebUtility.UrlEncode(expiresOn),
                    SharedAccessSignatureToken.SignedKeyName, WebUtility.UrlEncode(keyName));
            }

            static string BuildExpiresOn(TimeSpan timeToLive)
            {
                DateTime expiresOn = DateTime.UtcNow.Add(timeToLive);
                TimeSpan secondsFromBaseTime = expiresOn.Subtract(EpochTime);
                long seconds = Convert.ToInt64(secondsFromBaseTime.TotalSeconds, CultureInfo.InvariantCulture);
                return Convert.ToString(seconds, CultureInfo.InvariantCulture);
            }

            static string Sign(string requestString, byte[] encodedSharedAccessKey)
            {
                using (var hmac = new HMACSHA256(encodedSharedAccessKey))
                {
                    return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(requestString)));
                }
            }
        }

        /// <summary>
        /// A WCF SecurityToken that wraps a Shared Access Signature
        /// </summary>
        class SharedAccessSignatureToken : SecurityToken
        {
            public const int MaxKeyNameLength = 256;
            public const int MaxKeyLength = 256;
            public const string SharedAccessSignature = "SharedAccessSignature";
            public const string SignedResource = "sr";
            public const string Signature = "sig";
            public const string SignedKeyName = "skn";
            public const string SignedExpiry = "se";
            public const string SignedResourceFullFieldName = SharedAccessSignature + " " + SignedResource;
            public const string SasKeyValueSeparator = "=";
            public const string SasPairSeparator = "&";

            public SharedAccessSignatureToken(string tokenString)
                : base(
                      tokenString,
                      audienceFieldName: SignedResourceFullFieldName,
                      expiresOnFieldName: SignedExpiry,
                      keyValueSeparator: SasKeyValueSeparator,
                      pairSeparator: SasPairSeparator)
            {
            }

            internal static void Validate(string sharedAccessSignature)
            {
                if (string.IsNullOrEmpty(sharedAccessSignature))
                {
                    throw RelayEventSource.Log.ArgumentNull(nameof(sharedAccessSignature));
                }

                IDictionary<string, string> parsedFields = ExtractFieldValues(sharedAccessSignature);

                string signature;
                if (!parsedFields.TryGetValue(Signature, out signature))
                {
                    throw RelayEventSource.Log.ArgumentNull(Signature);
                }

                string expiry;
                if (!parsedFields.TryGetValue(SignedExpiry, out expiry))
                {
                    throw RelayEventSource.Log.ArgumentNull(SignedExpiry);
                }

                string keyName;
                if (!parsedFields.TryGetValue(SignedKeyName, out keyName))
                {
                    throw RelayEventSource.Log.ArgumentNull(SignedKeyName);
                }

                string encodedAudience;
                if (!parsedFields.TryGetValue(SignedResource, out encodedAudience))
                {
                    throw RelayEventSource.Log.ArgumentNull(SignedResource);
                }
            }

            static IDictionary<string, string> ExtractFieldValues(string sharedAccessSignature)
            {
                string[] tokenLines = sharedAccessSignature.Split();

                if (!string.Equals(tokenLines[0].Trim(), SharedAccessSignature, StringComparison.OrdinalIgnoreCase) || tokenLines.Length != 2)
                {
                    throw RelayEventSource.Log.ArgumentNull(nameof(sharedAccessSignature));
                }

                IDictionary<string, string> parsedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string[] tokenFields = tokenLines[1].Trim().Split(new string[] { SasPairSeparator }, StringSplitOptions.None);

                foreach (string tokenField in tokenFields)
                {
                    if (tokenField != string.Empty)
                    {
                        string[] fieldParts = tokenField.Split(new string[] { SasKeyValueSeparator }, StringSplitOptions.None);
                        if (string.Equals(fieldParts[0], SignedResource, StringComparison.OrdinalIgnoreCase))
                        {
                            // We need to preserve the casing of the escape characters in the audience,
                            // so defer decoding the URL until later.
                            parsedFields.Add(fieldParts[0], fieldParts[1]);
                        }
                        else
                        {
                            parsedFields.Add(fieldParts[0], WebUtility.UrlDecode(fieldParts[1]));
                        }
                    }
                }

                return parsedFields;
            }
        }
    }
}