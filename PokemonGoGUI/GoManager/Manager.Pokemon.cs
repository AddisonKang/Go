﻿using POGOProtos.Data;
using POGOProtos.Enums;
using POGOProtos.Inventory;
using POGOProtos.Networking.Responses;
using POGOProtos.Settings.Master;
using PokemonGo.RocketAPI;
using PokemonGo.RocketAPI.Helpers;
using PokemonGoGUI.Enums;
using PokemonGoGUI.GoManager.Models;
using PokemonGoGUI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PokemonGoGUI.GoManager
{
    public partial class Manager
    {
        public async Task<MethodResult> TransferPokemon(IEnumerable<PokemonData> pokemonToTransfer)
        {
            foreach (PokemonData pokemon in pokemonToTransfer)
            {
                try
                {
                    ReleasePokemonResponse releaseResponse = await _client.Inventory.TransferPokemon(pokemon.Id);

                    if (releaseResponse.Result != ReleasePokemonResponse.Types.Result.Success)
                    {
                        LogCaller(new LoggerEventArgs(String.Format("Failed to transfer pokemon {0}. Response: {1}", pokemon.PokemonId, releaseResponse.Result), LoggerTypes.Warning));
                    }
                    else
                    {
                        LogCaller(new LoggerEventArgs(
                            String.Format("Successully transferred {0}. Cp: {1}. IV: {2:0.00}%",
                                        pokemon.PokemonId,
                                        pokemon.Cp,
                                        CalculateIVPerfection(pokemon).Data),
                                        LoggerTypes.Transfer));
                    }

                    await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    LogCaller(new LoggerEventArgs("Transfer request failed", LoggerTypes.Exception, ex));
                }
            }

            return new MethodResult
            {
                Success = true
            };
        }

        private async Task<MethodResult> TransferFilteredPokemon()
        {
            MethodResult<List<PokemonData>> transferResult = await GetPokemonToTransfer();

            if(!transferResult.Success || transferResult.Data.Count == 0)
            {
                return new MethodResult();
            }


            LogCaller(new LoggerEventArgs(String.Format("Transferring {0} pokemon", transferResult.Data.Count), LoggerTypes.Info));

            await TransferPokemon(transferResult.Data);

            return new MethodResult
            {
                Success = true,
                Message = "Success"
            };
        }

        private async Task<MethodResult<List<PokemonData>>> GetPokemonToTransfer()
        {
            if (!UserSettings.TransferPokemon)
            {
                LogCaller(new LoggerEventArgs("Transferring disabled", LoggerTypes.Debug));

                return new MethodResult<List<PokemonData>>
                {
                    Data = new List<PokemonData>(),
                    Message = "Transferring disabled",
                    Success = true
                };
            }

            await UpdatePokemon(false);
            await UpdatePokemonCandy(false);
            await GetItemTemplates();

            if(Pokemon == null || Pokemon.Count == 0)
            {
                LogCaller(new LoggerEventArgs("You have no pokemon", LoggerTypes.Info));

                return new MethodResult<List<PokemonData>>
                {
                    Message = "You have no pokemon"
                };
            }

            List<PokemonData> pokemonToTransfer = new List<PokemonData>();

            IEnumerable<IGrouping<PokemonId, PokemonData>> groupedPokemon = Pokemon.GroupBy(x => x.PokemonId);

            foreach(IGrouping<PokemonId, PokemonData> group in groupedPokemon)
            {
                TransferSetting settings = UserSettings.TransferSettings.FirstOrDefault(x => x.Id == group.Key);

                if(settings == null)
                {
                    LogCaller(new LoggerEventArgs(String.Format("Failed to find transfer settings for pokemon {0}", group.Key), LoggerTypes.Warning));

                    continue;
                }

                if(!settings.Transfer)
                {
                    continue;
                }

                switch(settings.Type)
                {
                    case TransferType.All:
                        pokemonToTransfer.AddRange(group.ToList());
                        break;
                    case TransferType.BelowCP:
                        pokemonToTransfer.AddRange(GetPokemonBelowCP(group, settings.MinCP));
                        break;
                    case TransferType.BelowIVPercentage:
                        pokemonToTransfer.AddRange(GetPokemonBelowIVPercent(group, settings.CPPercent));
                        break;
                    case TransferType.KeepPossibleEvolves:
                        pokemonToTransfer.AddRange(GetPokemonByPossibleEvolve(group, settings.KeepMax));
                        break;
                    case TransferType.KeepStrongestX:
                        pokemonToTransfer.AddRange(GetPokemonByStrongest(group, settings.KeepMax));
                        break;
                }
            }

            return new MethodResult<List<PokemonData>>
            {
                Data = pokemonToTransfer,
                Message = String.Format("Found {0} pokemon to transfer", pokemonToTransfer.Count),
                Success = true
            };
        }

        private List<PokemonData> GetPokemonBelowCP(IGrouping<PokemonId, PokemonData> pokemon, int minCp)
        {
            return pokemon.Where(x => x.Cp < minCp).ToList();
        }

        private List<PokemonData> GetPokemonBelowIVPercent(IGrouping<PokemonId, PokemonData> pokemon, double percent)
        {
            List<PokemonData> toTransfer = new List<PokemonData>();

            foreach(PokemonData pData in pokemon)
            {
                MethodResult<double> perfectResult = CalculateIVPerfection(pData);

                if(!perfectResult.Success)
                {
                    continue;
                }

                if(perfectResult.Data >= 0 && perfectResult.Data < percent)
                {
                    toTransfer.Add(pData);
                }
            }

            return toTransfer;
        }

        private List<PokemonData> GetPokemonByStrongest(IGrouping<PokemonId, PokemonData> pokemon, int amount)
        {
            return pokemon.OrderByDescending(x => x.Cp).Skip(amount).ToList();
        }

        private List<PokemonData> GetPokemonByPossibleEvolve(IGrouping<PokemonId, PokemonData> pokemon, int limit)
        {
            PokemonSettings setting = null;

            if (!PokeSettings.TryGetValue(pokemon.Key, out setting))
            {
                LogCaller(new LoggerEventArgs(String.Format("Failed to find settings for pokemon {0}", pokemon.Key), LoggerTypes.Info));

                return new List<PokemonData>();
            }

            Candy pokemonCandy = PokemonCandy.FirstOrDefault(x => x.FamilyId == setting.FamilyId);

            int candyToEvolve = setting.CandyToEvolve;
            int totalPokemon = pokemon.Count();
            int totalCandy = pokemonCandy.Candy_;

            if(candyToEvolve == 0)
            {
                return new List<PokemonData>();
            }

            int maxPokemon = totalCandy / candyToEvolve;

            if(maxPokemon > limit)
            {
                maxPokemon = limit;
            }

            return pokemon.OrderByDescending(x => x.Cp).Skip(maxPokemon).ToList();
        }

        public MethodResult<double> CalculateIVPerfection(PokemonData pokemon)
        {
            MethodResult<PokemonSettings> settingResult = GetPokemonSetting(pokemon.PokemonId);

            if (!settingResult.Success)
            {
                return new MethodResult<double>
                {
                    Data = -1,
                    Message = settingResult.Message
                };
            }

            /*
            if (Math.Abs(pokemon.CpMultiplier + pokemon.AdditionalCpMultiplier) <= 0)
            {
                double perfection = (double)(pokemon.IndividualAttack * 2 + pokemon.IndividualDefense + pokemon.IndividualStamina) / (4.0 * 15.0) * 100.0;

                return new MethodResult<double>
                {
                    Data = perfection,
                    Message = "Success",
                    Success = true
                };
            }*/

            double maxCp = CalculateMaxCpMultiplier(pokemon);
            double minCp = CalculateMinCpMultiplier(pokemon);
            double curCp = CalculateCpMultiplier(pokemon);

            double perfectPercent = (curCp - minCp) / (maxCp - minCp) * 100.0;

            return new MethodResult<double>
            {
                Data = perfectPercent,
                Message = "Success",
                Success = true
            };
        }

        private double CalculateMaxCpMultiplier(PokemonData poke)
        {
            PokemonSettings pokemonSettings = GetPokemonSetting(poke.PokemonId).Data;

            return (pokemonSettings.Stats.BaseAttack + 15) * Math.Sqrt(pokemonSettings.Stats.BaseDefense + 15) *
                   Math.Sqrt(pokemonSettings.Stats.BaseStamina + 15);
        }

        private double CalculateCpMultiplier(PokemonData poke)
        {
            PokemonSettings pokemonSettings = GetPokemonSetting(poke.PokemonId).Data;

            return (pokemonSettings.Stats.BaseAttack + poke.IndividualAttack) *
                   Math.Sqrt(pokemonSettings.Stats.BaseDefense + poke.IndividualDefense) *
                   Math.Sqrt(pokemonSettings.Stats.BaseStamina + poke.IndividualStamina);
        }

        private double CalculateMinCpMultiplier(PokemonData poke)
        {
            PokemonSettings pokemonSettings = GetPokemonSetting(poke.PokemonId).Data;

            return pokemonSettings.Stats.BaseAttack * Math.Sqrt(pokemonSettings.Stats.BaseDefense) * Math.Sqrt(pokemonSettings.Stats.BaseStamina);
        }
    }
}
