//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// |
// Copyright 2015-2019 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Composition.Convention;
using System.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ArchiSteamFarm.Json;
using ArchiSteamFarm.Localization;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using SteamKit2;

namespace ArchiSteamFarm.Plugins {
	internal static class Core {
		[ImportMany]
		private static ImmutableHashSet<IPlugin> ActivePlugins { get; set; }

		internal static bool BotUsesNewChat(Bot bot) {
			if (bot == null) {
				ASF.ArchiLogger.LogNullError(nameof(bot));

				return false;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return true;
			}

			foreach (IBotHackNewChat plugin in ActivePlugins.OfType<IBotHackNewChat>()) {
				try {
					if (!plugin.BotUsesNewChat(bot)) {
						return false;
					}
				} catch (Exception e) {
					ASF.ArchiLogger.LogGenericException(e);
				}
			}

			return true;
		}

		[ItemNotNull]
		internal static async Task<StringComparer> GetBotsComparer() {
			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return StringComparer.Ordinal;
			}

			IList<StringComparer> results;

			try {
				results = await Utilities.InParallel(ActivePlugins.OfType<IBotsComparer>().Select(plugin => Task.Run(() => plugin.BotsComparer))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return StringComparer.Ordinal;
			}

			StringComparer result = results.FirstOrDefault(comparer => comparer != null);

			return result ?? StringComparer.Ordinal;
		}

		internal static bool InitPlugins() {
			HashSet<Assembly> assemblies = new HashSet<Assembly>();

			string pluginsPath = Path.Combine(SharedInfo.HomeDirectory, SharedInfo.PluginsDirectory);

			if (Directory.Exists(pluginsPath)) {
				HashSet<Assembly> loadedAssemblies = LoadAssembliesFrom(pluginsPath);

				if ((loadedAssemblies != null) && (loadedAssemblies.Count > 0)) {
					assemblies.UnionWith(loadedAssemblies);
				}
			}

			string customPluginsPath = Path.Combine(Directory.GetCurrentDirectory(), SharedInfo.PluginsDirectory);

			if (Directory.Exists(customPluginsPath)) {
				HashSet<Assembly> loadedAssemblies = LoadAssembliesFrom(customPluginsPath);

				if ((loadedAssemblies != null) && (loadedAssemblies.Count > 0)) {
					assemblies.UnionWith(loadedAssemblies);
				}
			}

			if (assemblies.Count == 0) {
				ASF.ArchiLogger.LogGenericTrace(Strings.NothingFound);

				return true;
			}

			ASF.ArchiLogger.LogGenericInfo(string.Format(Strings.Initializing, nameof(Plugins)));

			ConventionBuilder conventions = new ConventionBuilder();
			conventions.ForTypesDerivedFrom<IPlugin>().Export<IPlugin>();

			ContainerConfiguration configuration = new ContainerConfiguration().WithAssemblies(assemblies, conventions);

			HashSet<IPlugin> activePlugins;

			try {
				using (CompositionHost container = configuration.CreateContainer()) {
					activePlugins = container.GetExports<IPlugin>().ToHashSet();
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return false;
			}

			if (activePlugins.Count == 0) {
				return true;
			}

			HashSet<IPlugin> invalidPlugins = new HashSet<IPlugin>();

			foreach (IPlugin plugin in activePlugins) {
				try {
					string pluginName = plugin.Name;

					ASF.ArchiLogger.LogGenericInfo(string.Format(Strings.PluginLoading, pluginName, plugin.Version));
					plugin.OnLoaded();
					ASF.ArchiLogger.LogGenericInfo(string.Format(Strings.PluginLoaded, pluginName));
				} catch (Exception e) {
					ASF.ArchiLogger.LogGenericException(e);
					invalidPlugins.Add(plugin);
				}
			}

			if (invalidPlugins.Count > 0) {
				activePlugins.ExceptWith(invalidPlugins);

				if (activePlugins.Count == 0) {
					return false;
				}
			}

			ActivePlugins = activePlugins.ToImmutableHashSet();
			ASF.ArchiLogger.LogGenericInfo(Strings.PluginsWarning);

			return invalidPlugins.Count == 0;
		}

		internal static async Task OnASFInitModules(IReadOnlyDictionary<string, JToken> additionalConfigProperties = null) {
			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return;
			}

			try {
				await Utilities.InParallel(ActivePlugins.OfType<IASF>().Select(plugin => Task.Run(() => plugin.OnASFInit(additionalConfigProperties)))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		[ItemCanBeNull]
		internal static async Task<string> OnBotCommand(Bot bot, ulong steamID, string message, string[] args) {
			if ((bot == null) || (steamID == 0) || string.IsNullOrEmpty(message) || (args == null) || (args.Length == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(args));

				return null;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return null;
			}

			IList<string> responses;

			try {
				responses = await Utilities.InParallel(ActivePlugins.OfType<IBotCommand>().Select(plugin => plugin.OnBotCommand(bot, steamID, message, args))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return null;
			}

			return string.Join(Environment.NewLine, responses.Where(response => !string.IsNullOrEmpty(response)));
		}

		internal static async Task OnBotDestroy(Bot bot) {
			if (bot == null) {
				ASF.ArchiLogger.LogNullError(nameof(bot));

				return;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return;
			}

			try {
				await Utilities.InParallel(ActivePlugins.OfType<IBot>().Select(plugin => Task.Run(() => plugin.OnBotDestroy(bot)))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		internal static async Task OnBotDisconnected(Bot bot, EResult reason) {
			if (bot == null) {
				ASF.ArchiLogger.LogNullError(nameof(bot));

				return;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return;
			}

			try {
				await Utilities.InParallel(ActivePlugins.OfType<IBotConnection>().Select(plugin => Task.Run(() => plugin.OnBotDisconnected(bot, reason)))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		internal static async Task<bool> OnBotFriendRequest(Bot bot, ulong steamID) {
			if ((bot == null) || (steamID == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(steamID));

				return false;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return false;
			}

			IList<bool> responses;

			try {
				responses = await Utilities.InParallel(ActivePlugins.OfType<IBotFriendRequest>().Select(plugin => plugin.OnBotFriendRequest(bot, steamID))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return false;
			}

			return responses.Any(response => response);
		}

		internal static async Task OnBotInit(Bot bot) {
			if (bot == null) {
				ASF.ArchiLogger.LogNullError(nameof(bot));

				return;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return;
			}

			try {
				await Utilities.InParallel(ActivePlugins.OfType<IBot>().Select(plugin => Task.Run(() => plugin.OnBotInit(bot)))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		internal static async Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JToken> additionalConfigProperties = null) {
			if (bot == null) {
				ASF.ArchiLogger.LogNullError(nameof(bot));

				return;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return;
			}

			try {
				await Utilities.InParallel(ActivePlugins.OfType<IBotModules>().Select(plugin => Task.Run(() => plugin.OnBotInitModules(bot, additionalConfigProperties)))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		internal static async Task OnBotLoggedOn(Bot bot) {
			if (bot == null) {
				ASF.ArchiLogger.LogNullError(nameof(bot));

				return;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return;
			}

			try {
				await Utilities.InParallel(ActivePlugins.OfType<IBotConnection>().Select(plugin => Task.Run(() => plugin.OnBotLoggedOn(bot)))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		[ItemCanBeNull]
		internal static async Task<string> OnBotMessage(Bot bot, ulong steamID, string message) {
			if ((bot == null) || (steamID == 0) || string.IsNullOrEmpty(message)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(message));

				return null;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return null;
			}

			IList<string> responses;

			try {
				responses = await Utilities.InParallel(ActivePlugins.OfType<IBotMessage>().Select(plugin => plugin.OnBotMessage(bot, steamID, message))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return null;
			}

			return string.Join(Environment.NewLine, responses.Where(response => !string.IsNullOrEmpty(response)));
		}

		internal static async Task<bool> OnBotTradeOffer(Bot bot, Steam.TradeOffer tradeOffer) {
			if ((bot == null) || (tradeOffer == null)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(tradeOffer));

				return false;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return false;
			}

			IList<bool> responses;

			try {
				responses = await Utilities.InParallel(ActivePlugins.OfType<IBotTradeOffer>().Select(plugin => plugin.OnBotTradeOffer(bot, tradeOffer))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return false;
			}

			return responses.Any(response => response);
		}

		internal static async Task OnBotTradeOfferResults(Bot bot, IReadOnlyCollection<Trading.ParseTradeResult> tradeResults) {
			if ((bot == null) || (tradeResults == null) || (tradeResults.Count == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(tradeResults));

				return;
			}

			if ((ActivePlugins == null) || (ActivePlugins.Count == 0)) {
				return;
			}

			try {
				await Utilities.InParallel(ActivePlugins.OfType<IBotTradeOfferResults>().Select(plugin => Task.Run(() => plugin.OnBotTradeOfferResults(bot, tradeResults)))).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		private static HashSet<Assembly> LoadAssembliesFrom(string path) {
			if (string.IsNullOrEmpty(path)) {
				ASF.ArchiLogger.LogNullError(nameof(path));

				return null;
			}

			if (!Directory.Exists(path)) {
				return null;
			}

			HashSet<Assembly> assemblies = new HashSet<Assembly>();

			try {
				foreach (string assemblyPath in Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories)) {
					Assembly assembly;

					try {
						assembly = Assembly.LoadFrom(assemblyPath);
					} catch (Exception e) {
						ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, assemblyPath));
						ASF.ArchiLogger.LogGenericWarningException(e);

						continue;
					}

					assemblies.Add(assembly);
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return null;
			}

			return assemblies;
		}
	}
}
