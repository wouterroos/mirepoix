//
// Author:
//   Aaron Bockover <abock@xamarin.com>
//
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Xamarin.Security.Keychains
{
    /// <summary>
    /// An <see cref="IKeychain"/> implementation backed by <see cref="AppleKeychain"/> on macOS,
    /// <see cref="DPAPIKeychain"/> on Windows, and <see cref="ManagedProtectionKeychain"/> on
    /// other platforms.
    /// </summary>
    [EditorBrowsable (EditorBrowsableState.Advanced)]
    public sealed class OSKeychain : IKeychain
    {
        readonly IKeychain keychain;

        public OSKeychain ()
        {
            if (RuntimeInformation.IsOSPlatform (OSPlatform.OSX))
                keychain = new AppleKeychain ();
            else if (RuntimeInformation.IsOSPlatform (OSPlatform.Windows))
                keychain = new DPAPIKeychain ();
            else
                keychain = new ManagedProtectionKeychain ();
        }

        public bool TryGetSecret (KeychainSecretName name, out KeychainSecret secret)
            => keychain.TryGetSecret (name, out secret);

        public void StoreSecret (KeychainSecret secret, bool updateExisting = true)
            => keychain.StoreSecret (secret, updateExisting);
    }
}