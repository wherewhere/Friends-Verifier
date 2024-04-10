using FriendsVerifier.Common;
using FriendsVerifier.Models;
using FriendsVerifier.Properties.Resource;
using Microsoft.Extensions.Configuration;
using OtpNet;
using QRCoder;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static QRCoder.QRCodeGenerator;

namespace FriendsVerifier
{
    public static class Program
    {
        private static readonly IConfigurationRoot Configuration = new ConfigurationBuilder().AddWritableJsonFile(Path.ChangeExtension(nameof(FriendsVerifier), "json"), true, true).Build();

        public static Task<int> Main(string[] args)
        {
            InitializeSettings();
            InitializeLanguage();

            CliArgument<string> nameArgument = new("name")
            {
                Arity = ArgumentArity.ExactlyOne,
                Description = Resource.NameArgumentDescription,
                HelpName = Resource.NameArgument
            };
            nameArgument.CompletionSources.Add(
                x => Configuration["Users"] is string users
                     && JsonSerializer.Deserialize(users, SourceGenerationContext.Default.DictionaryStringString) is Dictionary<string, string> list ?
                     from name in list.Keys
                     where string.IsNullOrWhiteSpace(x.WordToComplete) || name.StartsWith(x.WordToComplete.Trim('\'', '\"', ' '), StringComparison.OrdinalIgnoreCase)
                     select name.Contains(' ') ? $"'{name}'" : name : []);

            CliOption<string> passkeyOption = new("--passkey", "-p")
            {
                Arity = ArgumentArity.ExactlyOne,
                Description = Resource.PasskeyOptionDescription,
                HelpName = Resource.PasskeyOption,
                Required = true
            };

            CliCommand addCommand = new("add", Resource.AddCommandDescription)
            {
                nameArgument,
                passkeyOption
            };
            addCommand.SetAction(x => AddCommandHandler(x.GetValue(nameArgument), x.GetValue(passkeyOption)));

            CliOption<OutputType> typeOption = new("--type", "-t")
            {
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = _ => OutputType.Url,
                Description = Resource.TypeOptionDescription,
                HelpName = Resource.TypeOption
            };

            CliCommand outputCommand = new("output", Resource.OutputCommandDescription)
            {
                nameArgument,
                typeOption
            };
            outputCommand.SetAction(x => OutputCommandHandler(x.GetValue(nameArgument), x.GetValue(typeOption)));

            CliOption<int> codeOption = new("--code", "-c")
            {
                Arity = ArgumentArity.ExactlyOne,
                Description = Resource.CodeOptionDescription,
                HelpName = Resource.CodeOption,
                Required = true,
            };

            CliOption<DateTimeOffset> timeOption = new("--time", "-t")
            {
                Arity = ArgumentArity.ZeroOrOne,
                CustomParser = x =>
                {
                    if (x.Tokens[0] is CliToken token)
                    {
                        if (long.TryParse(token.Value, out long number))
                        {
                            return DateTimeOffset.FromUnixTimeSeconds(number);
                        }
                        else
                        {
                            try
                            {
                                return DateTimeOffset.Parse(token.Value, CultureInfo.CurrentCulture);
                            }
                            catch (Exception ex)
                            {
                                x.AddError(ex.ToString());
                            }
                        }
                    }
                    return DateTimeOffset.UtcNow;
                },
                DefaultValueFactory = _ => DateTimeOffset.Now,
                Description = Resource.TimeOptionDescription,
                HelpName = Resource.TimeOption
            };
            timeOption.CompletionSources.Add(_ => [$"'{DateTimeOffset.Now}'"]);

            CliCommand verifyCommand = new("verify", Resource.VerifyCommandDescription)
            {
                nameArgument,
                codeOption,
                timeOption
            };
            verifyCommand.SetAction(x => VerifyCommandHandler(x.GetValue(nameArgument), x.GetValue(codeOption), x.GetValue(timeOption)));

            CliCommand removeCommand = new("remove", Resource.RemoveCommandDescription)
            {
                nameArgument
            };
            removeCommand.SetAction(x => RemoveCommandHandler(x.GetValue(nameArgument)));

            CliArgument<string> langArgument = new("code")
            {
                Arity = ArgumentArity.ExactlyOne,
                Description = Resource.LangArgumentDescription,
                HelpName = Resource.LangArgument
            };
            langArgument.CompletionSources.Add(
                x => from code in (IEnumerable<string>)["default", "zh-CN", "en-US"]
                     where string.IsNullOrWhiteSpace(x.WordToComplete) || code.StartsWith(x.WordToComplete.Trim('\'', ' '), StringComparison.OrdinalIgnoreCase)
                     select code);

            CliCommand langCommand = new("lang", Resource.LangCommandDescription)
            {
                langArgument,
            };
            langCommand.SetAction(x => LangCommandHandler(x.GetValue(langArgument)));

            CliRootCommand rootCommand = new(Resource.RootCommandDescription)
            {
                addCommand,
                outputCommand,
                verifyCommand,
                removeCommand,
                langCommand
            };

            return new CliConfiguration(rootCommand).InvokeAsync(args);
        }

        private static void InitializeSettings()
        {
            if (string.IsNullOrEmpty(Configuration["Users"]))
            {
                Configuration["Users"] = JsonSerializer.Serialize([], SourceGenerationContext.Default.DictionaryStringString);
            }
            if (string.IsNullOrEmpty(Configuration["Language"]))
            {
                Configuration["Language"] = "default";
            }
        }

        private static void InitializeLanguage()
        {
            string code = Configuration["Language"];
            if (!code.Equals("default"))
            {
                try
                {
                    CultureInfo culture = new(code);
                    CultureInfo.DefaultThreadCurrentCulture = culture;
                    CultureInfo.DefaultThreadCurrentUICulture = culture;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Configuration["Language"] = "default";
                }
            }
        }

        private static void AddCommandHandler(string name, string passkey)
        {
            string format;
            if (Configuration["Users"] is string users)
            {
                Dictionary<string, string> list = JsonSerializer.Deserialize(users, SourceGenerationContext.Default.DictionaryStringString);
                bool exist = list.ContainsKey(name);
                list[name] = passkey;
                Configuration["Users"] = JsonSerializer.Serialize(list, SourceGenerationContext.Default.DictionaryStringString);
                format = exist ? Resource.UpdateSucceedFormat : Resource.AddSucceedFormat;
            }
            else
            {
                Configuration["Users"] = JsonSerializer.Serialize(new() { { name, passkey } }, SourceGenerationContext.Default.DictionaryStringString);
                format = Resource.AddSucceedFormat;
            }
            Console.WriteLine(format, name, new OtpUri(OtpType.Totp, $"{name}{passkey}".GetBase64(), name, "Friends Verifier"));
        }

        private static void OutputCommandHandler(string name, OutputType outputType)
        {
            if (Configuration["Users"] is string users)
            {
                Dictionary<string, string> list = JsonSerializer.Deserialize(users, SourceGenerationContext.Default.DictionaryStringString);
                if (list.TryGetValue(name, out string passkey))
                {
                    if (outputType == OutputType.Code)
                    {
                        Console.WriteLine(new Totp($"{name}{passkey}".GetBase64()).ComputeTotp());
                    }
                    else
                    {
                        OtpUri otp = new(OtpType.Totp, $"{name}{passkey}".GetBase64(), name, "Friends Verifier");
                        switch (outputType)
                        {
                            case OutputType.Url:
                                Console.WriteLine(otp);
                                break;
                            case OutputType.QrCode:
                                using (QRCodeGenerator qrGenerator = new())
                                {
                                    QRCodeData data = qrGenerator.CreateQrCode(otp.ToString(), ECCLevel.Q);
                                    using AsciiQRCode qrCode = new(data);
                                    string qrCodeImage = qrCode.GetGraphic(1, drawQuietZones: false);
                                    Console.WriteLine(qrCodeImage);
                                }
                                break;
                        }
                    }
                    return;
                }
            }
            Console.WriteLine(Resource.FriendNotFoundFormat, name);
        }

        private static void VerifyCommandHandler(string name, int code, DateTimeOffset dateTimeOffset)
        {
            if (Configuration["Users"] is string users)
            {
                Dictionary<string, string> list = JsonSerializer.Deserialize(users, SourceGenerationContext.Default.DictionaryStringString);
                if (list.TryGetValue(name, out string passkey))
                {
                    if (new Totp($"{name}{passkey}".GetBase64()).VerifyTotp(dateTimeOffset.UtcDateTime, code.ToString(), out _))
                    {
                        Console.WriteLine(Resource.VerifySucceedFormat, name);
                    }
                    else
                    {
                        Console.WriteLine(Resource.VerifyFailedFormat);
                    }
                    return;
                }
            }
            Console.WriteLine(Resource.VerifyNotFoundFormat);
        }

        private static void RemoveCommandHandler(string name)
        {
            if (Configuration["Users"] is string users)
            {
                Dictionary<string, string> list = JsonSerializer.Deserialize(users, SourceGenerationContext.Default.DictionaryStringString);
                if (list.ContainsKey(name))
                {
                    list.Remove(name);
                    Configuration["Users"] = JsonSerializer.Serialize(list, SourceGenerationContext.Default.DictionaryStringString);
                    Console.WriteLine(Resource.RemoveSucceedFormat, name);
                    return;
                }
            }
            Console.WriteLine(Resource.FriendNotFoundFormat, name);
        }

        private static void LangCommandHandler(string code)
        {
            if (code.Equals("null", StringComparison.OrdinalIgnoreCase) || code.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                CultureInfo.DefaultThreadCurrentCulture = null;
                CultureInfo.DefaultThreadCurrentUICulture = null;
                Configuration["Language"] = "default";
            }
            else
            {
                CultureInfo culture = new(code);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                Configuration["Language"] = code;
            }
            Console.WriteLine(string.Format(Resource.CurrentLanguageChangedFormat, CultureInfo.CurrentCulture.DisplayName));
        }

        private static byte[] GetBase64(this string input) => Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(input)));
    }

    [JsonSerializable(typeof(Dictionary<string, string>))]
    public partial class SourceGenerationContext : JsonSerializerContext;
}
