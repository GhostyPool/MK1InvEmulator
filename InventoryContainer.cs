using Fluxzy.Rules.Filters.ResponseFilters;
using Org.BouncyCastle.Asn1.Cms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using static Emulator.Debug;
using static HydraDotNet.Core.Models.ItemSlugsArray;
using static HydraDotNet.Core.Models.PlayerInventoryItem;

namespace Emulator
{
    internal static class InventoryContainer
    {
        private static byte[] inventory = loadInventoryFile();

        private static readonly Dictionary<object, object?> inventoryDict = HydraHelpers.decodeFromHydra(inventory);

        private readonly static string accountId = InvHelpers.generateRandomId();

        public static byte[] Inventory => inventory;
        public static Dictionary<object, object?>? LastRequest { get; set; }

        private static byte[] loadInventoryFile()
        {
            if (!File.Exists("inventory\\inventory.bin")) Debug.exitWithError("ERROR: Required file 'inventory.bin' is missing! Please re-download the program and make sure to keep all files!");

            return File.ReadAllBytes("inventory\\inventory.bin");
        }

        public static async Task<byte[]?> createKustomizeResponse()
        {
            if (LastRequest != null)
            {
                var updatedItems = checkRequest();

                //Create patched response
                return await HydraHelpers.encodeFromDictionary(createResponseDictionary(updatedItems));
            }
            else
            {
                printError("ERROR: Last request was not saved or is null!");
                return null;
            }
        }

        private static Dictionary<object, object?> createResponseDictionary(List<object> updatedItems)
        {
            Dictionary<object, object?> newResponseDict = new()
            {
                ["body"] = new Dictionary<object, object?>
                {
                    ["transaction"] = new Dictionary<object, object?>
                    {
                        ["transaction_id"] = Guid.NewGuid().ToString(),
                        ["hydra_events"] = new object[]
                    {
                        new Dictionary<object, object?>
                        {
                            ["auto_managed"] = true,
                            ["event_type"] = "inventory-event",
                            ["account_id"] = accountId,
                            ["timestamp"] = Random.Shared.NextInt64(),
                            ["payload"] = new Dictionary<object, object?>
                            {
                                ["current_items"] = new object[] {},
                                ["deleted_items"] = new object[] {}
                            }
                        }
                    },
                        ["client_version"] = "0.308",
                        ["client_platform"] = "win64"
                    },
                    ["account_id"] = accountId,
                    ["response"] = new Dictionary<object, object?>
                    {
                        ["current_items"] = new object[] { },
                        ["deleted_items"] = new object[] { }
                    }
                },
                ["metadata"] = new Dictionary<object, object?>
                {
                    ["msg"] = "ONLINE_RESULT_SUCCESS"
                },
                ["return_code"] = 0
            };

            //Copy updated items into response
            Object[] current_items_response = new Object[updatedItems.Count];
            Object[] current_items_payload = new Object[updatedItems.Count];

            for (int i = 0; i < updatedItems.Count; i++)
            {
                current_items_response[i] = updatedItems[i];
                current_items_payload[i] = new Dictionary<object, object?>((Dictionary<object, object?>)updatedItems[i]);

                //Change dates to accomodate for format
                ((Dictionary<object, object?>)current_items_payload[i])["updated_at"] = ((DateTime)((Dictionary<object, object?>)updatedItems[i])["updated_at"]).ToString("yyyy-MM-ddTHH:mm:ss");
                ((Dictionary<object, object?>)current_items_payload[i])["created_at"] = ((DateTime)((Dictionary<object, object?>)updatedItems[i])["created_at"]).ToString("yyyy-MM-ddTHH:mm:ss");
            }

            var payload = (Dictionary<object, object?>)((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)((Dictionary<object, object?>)newResponseDict["body"])["transaction"])["hydra_events"])[0])["payload"];

            payload["current_items"] = current_items_payload;

            var response = (Dictionary<object, object?>)((Dictionary<object, object?>)newResponseDict["body"])["response"];

            response["current_items"] = current_items_response;

            return newResponseDict;
        }

        private static List<object>? checkRequest()
        {
            List<object> updatedItems = new List<object>();

            var current_items = ((Dictionary<object, object?>)((Dictionary<object, object?>)inventoryDict["body"])["response"])["current_items"];

            if (current_items is Object[] itemObjects)
            {
                if (((Object[])LastRequest["update_loadout"]).Length != 0)
                {
                    //Loadout update
                    var charId = (string)((Dictionary<object, object?>)((Object[])LastRequest["update_loadout"])[0])["unique_id"];

                    debugLog("Character ID: " + charId);

                    //Update loadout stuff
                    updatedItems.Add(handleLoadoutUpdates(itemObjects, charId));
                }

                var favouriteItems = (Dictionary<object, object?>)LastRequest["is_favorite"];

                if (favouriteItems.Count != 0 )
                {
                    //Handle favouriting items
                    List<object> favouritedItems = handleFavourites(favouriteItems, itemObjects);

                    for (int i = 0; i < favouritedItems.Count; i++)
                    {
                        updatedItems.Add(favouritedItems[i]);
                    }
                }

                //Save changes to inv file
                debugLog("Saving changes locally...");
                saveChanges();
            }
            else
            {
                printError("ERROR: Current_items is not an Object[]!");
            }

            return updatedItems;
        }

        private static Dictionary<object, object?>? handleLoadoutUpdates(Object[] itemObjects, string charId)
        {
            var charDict = findItemById(charId, itemObjects);

            if (charDict == null)
            {
                printError(String.Format("ERROR: Character with ID: {0} is not valid!", charId));
                return null;
            }

            var currentSlotsDict = (Dictionary<object, object?>)((Dictionary<object, object?>)((Dictionary<object, object?>)charDict["data"])["slots"])["slots"];

            var slotItems = (Object[])((Dictionary<object, object?>)((Dictionary<object, object?>)((Object[])LastRequest["update_loadout"])[0])["differences"])["slotitem"];

            //Handle regular updates
            if (slotItems.Length != 0)
            {
                //Handle new items
                for (int i = 0; i < slotItems.Length; i++)
                {
                    var item = findItemById((string)slotItems[i], itemObjects);

                    if (item == null) 
                    {
                        printError("ERROR: Item could not be found! Please restart your game if you accidentally claimed new items!");
                        return null;
                    }

                    string itemType = InventoryContainer.getItemType(item);

                    if (itemType == null)
                    {
                        printError(String.Format("Item: {0} could not be found!", (string)slotItems[i]));
                        continue;
                    }

                    debugLog("Item type: " + itemType);

                    if (currentSlotsDict.TryGetValue(itemType, out object? targetItem))
                    {
                        var targetItemDict = (Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)targetItem)["items"])[0];

                        debugLog("Unique ID: " + (string)targetItemDict["uniqueId"]);

                        targetItemDict["uniqueId"] = slotItems[i];

                        debugLog("Patched unique ID to: " + targetItemDict["uniqueId"]);
                    }
                    else
                    {
                        debugLog(String.Format("Value {0} does not exist for characer ID: {1}. Creating... ", itemType, charId));

                        currentSlotsDict[itemType] = new Dictionary<object, object?>();

                        Object[] items = { new Dictionary<object, object?>() { ["uniqueId"] = slotItems[i] } };

                        ((Dictionary<object, object?>)currentSlotsDict[itemType]).Add("items", items);
                        ((Dictionary<object, object?>)currentSlotsDict[itemType]).Add("randomizeType", "None");

                        debugLog(String.Format("Successfully created new value for {0} and added {1} to it", itemType, slotItems[i]));
                    }
                }
            }

            var randomizeType = (Dictionary<object, object?>)((Dictionary<object, object?>)((Dictionary<object, object?>)((Object[])LastRequest["update_loadout"])[0])["differences"])["itemslotrandomizetype"];

            //Handle random items updates
            if (randomizeType.Count != 0)
            {
                foreach (var randomItem in randomizeType)
                {
                    ((Dictionary<object, object?>)currentSlotsDict[randomItem.Key])["randomizeType"] = randomItem.Value;

                    debugLog("Patched value of randomizeType: " + (string)((Dictionary<object, object?>)currentSlotsDict[randomItem.Key])["randomizeType"]);
                }
            }

            return charDict;
        }

        private static List<object>? handleFavourites(Dictionary<object, object?> favouriteItems, Object[] itemObjects)
        {
            List<object> updatedItems = new List<object>();

            //Find each item inside of the current items
            foreach (var favItem in favouriteItems)
            {
                var item = findItemById((string)favItem.Key, itemObjects);

                if (item == null)
                {
                    printError("ERROR: Item could not be found! Please restart your game if you accidentally claimed new items!");
                    return null;
                }

                //Set it as favourite
                ((Dictionary<object, object?>)item["data"])["bIsFavorite"] = true;

                debugLog(String.Format("Set item {0} to value of {1}", (string)favItem.Key, ((Dictionary<object, object?>)item["data"])["bIsFavorite"]));

                updatedItems.Add(item);
            }

            return updatedItems;
        }


        private static Dictionary<object, object?>? findItemById(string id, Object[] itemsArray)
        {

            debugLog("Item ID to look for: " + id);

            for (int i = 0; i < itemsArray.Length; i++)
            {
                if (itemsArray[i] is Dictionary<object, object?> itemDict
                    && itemDict.TryGetValue("id", out var itemId)
                    && itemId is string stringId)
                {

                    if (stringId.Equals(id))
                    {
                        debugLog("Found item with id: " + id);
                        return itemDict;
                    }
                }
            }

            return null;
        }

        private static Dictionary<object, object?>? findItemBySlugName(Object[] itemsArray, string slugName)
        {
            for (int i = 0; i < itemsArray.Length; i++)
            {
                if (itemsArray[i] is Dictionary<object, object?> itemDict
                    && itemDict.TryGetValue("item_slug", out object itemSlug)
                    && itemSlug is string slugString)
                {
                    if (slugString.Equals(slugName))
                    {
                        debugLog("Found item by slug: " + slugString);
                        return itemDict;
                    }
                }
            }

            return null;
        }

        private static string? getItemType(Dictionary<object, object?> itemDict)
        {
            if (((string)itemDict["item_slug"]).Contains("Gear"))
            {
                return "Gear";
            }
            else if (((string)itemDict["item_slug"]).Contains("Skin"))
            {
                return "Skin";
            }
            else if (((string)itemDict["item_slug"]).Contains("SeasonalFatality"))
            {
                return "SeasonalFatality";
            }

            return null;
        }

        private static async void saveChanges()
        {
            //Sync inventory with dictionary version
            inventory = await HydraHelpers.encodeFromDictionary(inventoryDict);
            debugLog("Synced in-memory inventory with dictionary version");

            if (!Directory.Exists("inventory"))
            {
                debugLog("Created inventory directory");
                Directory.CreateDirectory("inventory");
            }

            //Make backup of current inv
            try
            {
                await File.WriteAllBytesAsync("inventory\\inventory.bin.bak", inventory);
                debugLog("Successfully made a backup of the current inventory");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("WARNING: Could not save an inventory backup!");
                Console.WriteLine("WARNING: Full exception: " + ex);
                Console.ResetColor();
            }

            try
            {
                await File.WriteAllBytesAsync("inventory\\inventory.bin", inventory);
                debugLog("Successfully saved inventory");
            }
            catch (Exception ex) 
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("WARNING: Could not save changes to inventory locally! (Your recent changes might not be retained)");
                Console.WriteLine("WARNING: Full exception: " + ex);
                Console.ResetColor();
            }
        }

        public static void changePlayerLvl()
        {
            var current_items = (Object[])((Dictionary<object, object?>)((Dictionary<object, object?>)inventoryDict["body"])["response"])["current_items"];

            var profileDict = findItemBySlugName(current_items, "Profile");

            if (profileDict != null)
            {
                byte lvl = 0;
                ((Dictionary<object, object?>)((Dictionary<object, object?>)profileDict["data"])["currentLevel"])["val"] = lvl;
                debugLog("Set player level to " + lvl);

                ushort exp = 0;
                ((Dictionary<object, object?>)((Dictionary<object, object?>)profileDict["data"])["experience"])["val"] = exp;
                debugLog("Set player experience to " + exp);

                saveChanges();
            }
        }

        public static void syncPlayerLvl(Dictionary<object, object?> originalInv)
        {
            var customInvData = getPlayerLvlAndExp(inventoryDict);

            if (!customInvData.HasValue) return;

            (byte lvl, ushort? exp16, uint? exp32, Dictionary<object, object?> data)? userInvData;

            if (originalInv != null)
            {
                userInvData = getPlayerLvlAndExp(originalInv);

                if (!userInvData.HasValue) return;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("WARNING: Original invenory is null! Cannot sync inventory!");
                Console.ResetColor();
                return;
            }

            //Sync lvl and exp
            if (userInvData.Value.exp16.HasValue)
            {
                ((Dictionary<object, object?>)customInvData.Value.data["experience"])["val"] = userInvData.Value.exp16;
                debugLog("Synced player experience to: " + userInvData.Value.exp16);
            }
            else if (userInvData.Value.exp32.HasValue)
            {
                ((Dictionary<object, object?>)customInvData.Value.data["experience"])["val"] = userInvData.Value.exp32;
                debugLog("Synced player experience to: " + userInvData.Value.exp32);
            }
            else
            {
                printError("ERROR: Could not read valid experience data!");
                return;
            }

            ((Dictionary<object, object?>)customInvData.Value.data["currentLevel"])["val"] = userInvData.Value.lvl;
            debugLog("Synced player level to: " + userInvData.Value.lvl);

            saveChanges();
        }

        private static (byte lvl, ushort? exp16, uint? exp32, Dictionary<object, object?> data)? getPlayerLvlAndExp(Dictionary<object, object?> inv)
        {
            var current_items = (Object[])((Dictionary<object, object?>)((Dictionary<object, object?>)inv["body"])["response"])["current_items"];

            var profileDict = findItemBySlugName(current_items, "Profile");

            if (profileDict != null)
            {
                var data = (Dictionary<object, object?>)profileDict["data"];

                byte lvl = (byte)((Dictionary<object, object?>)data["currentLevel"])["val"];

                if (((Dictionary<object, object?>)data["experience"])["val"] is ushort exp16)
                {
                    debugLog(String.Format("Found player level: {0} and experience of type UInt16: {1}", lvl, exp16));

                    return (lvl, exp16, null, data);
                }
                else if (((Dictionary<object, object?>)data["experience"])["val"] is uint exp32)
                {
                    debugLog(String.Format("Found player level: {0} and experience of type UInt32: {1}", lvl, exp32));

                    return (lvl, null, exp32, data);
                }
                else
                {
                    printError("ERROR: Could not determine type of player experience!");
                }
            }
            else
            {
                printError("ERROR: Could not find player profile!");
            }

            return null;
        }

        public static void randomizeAccountId()
        {
            var body = (Dictionary<object, object?>)inventoryDict["body"];

            //Randomize GUID
            ((Dictionary<object, object?>)body["transaction"])["transaction_id"] = Guid.NewGuid().ToString();

            debugLog("New GUID: " + ((Dictionary<object, object?>)body["transaction"])["transaction_id"]);

            body["account_id"] = accountId;

            var current_items = (Object[])((Dictionary<object, object?>)body["response"])["current_items"];

            for (int i = 0; i < current_items.Length; i++)
            {
                if (((Dictionary<object, object?>)current_items[i]).TryGetValue("account_id", out var validAccountId))
                {
                    ((Dictionary<object, object?>)current_items[i])["account_id"] = accountId;
                }
            }

            debugLog("Account ID randomized successfully to: " + accountId);
            saveChanges();
        }

        public static void randomizeInventoryIds()
        {
            var body = (Dictionary<object, object?>)inventoryDict["body"];

            var current_items = (Object[])((Dictionary<object, object?>)body["response"])["current_items"];

            for (int i = 0; i < current_items.Length; i++)
            {
                if (((Dictionary<object, object?>)current_items[i]).TryGetValue("id", out var validId))
                {
                    ((Dictionary<object, object?>)current_items[i])["id"] = InvHelpers.generateRandomId();
                    debugLog("New itemID: " + ((Dictionary<object, object?>)current_items[i])["id"]);
                }
            }

            debugLog("Inventory IDs randomized successfully!");
            saveChanges();
        }
    }
}
