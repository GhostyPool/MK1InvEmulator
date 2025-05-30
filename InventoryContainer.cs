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
using static Emulator.InvHelpers;
using static HydraDotNet.Core.Models.ItemSlugsArray;
using static HydraDotNet.Core.Models.PlayerInventoryItem;

namespace Emulator
{
    internal static class InventoryContainer
    {
        private static readonly string appdataFolder = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MK1InvEmu")).FullName;

        private static byte[] inventory = loadInventoryFile();

        private static readonly Dictionary<object, object?> inventoryDict = HydraHelpers.decodeFromHydra(inventory);

        private readonly static string accountId = generateRandomId();

        public static byte[] Inventory => inventory;

        public static string AppdataFolder => appdataFolder;
        public static Dictionary<object, object?>? LastRequest { get; set; }

        private static byte[] loadInventoryFile()
        {
            string invFolder = "inventory";
            string invFileName = "inventory.bin";

            debugLog("Appdata path: " + appdataFolder);

            checkForUpdate(invFolder, invFileName, appdataFolder);

            return File.ReadAllBytes(Path.Combine(appdataFolder, invFolder, invFileName));
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
                printError("Last request was not saved or is null!");
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
                printError("Current_items is not an Object[]!");
            }

            return updatedItems;
        }

        private static Dictionary<object, object?>? handleLoadoutUpdates(Object[] itemObjects, string charId)
        {
            var charDict = findItemById(charId, itemObjects);

            if (charDict == null)
            {
                printError(String.Format("Character with ID: {0} is not valid!", charId));
                return null;
            }

            var currentSlotsDict = getEquippedItemsDict(charDict);

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
                        printError("Item could not be found! Please restart your game if you accidentally claimed new items!");
                        return null;
                    }

                    string itemType = getItemType(item);

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
                    printError("Item could not be found! Please restart your game if you accidentally claimed new items!");
                    return null;
                }

                //Set new value
                ((Dictionary<object, object?>)item["data"])["bIsFavorite"] = favItem.Value;

                debugLog(String.Format("Set item {0} to value of {1}", (string)favItem.Key, ((Dictionary<object, object?>)item["data"])["bIsFavorite"]));

                updatedItems.Add(item);
            }

            return updatedItems;
        }

        private static async void saveChanges()
        {
            //Sync inventory with dictionary version
            inventory = await HydraHelpers.encodeFromDictionary(inventoryDict);
            debugLog("Synced in-memory inventory with dictionary version");

            string invAppdataFolder = Path.Combine(appdataFolder, "inventory");

            if (!Directory.Exists(invAppdataFolder))
            {
                debugLog("Created inventory directory");
                Directory.CreateDirectory(invAppdataFolder);
            }

            //Make backup of current inv
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(invAppdataFolder, "inventory.bin.bak"), inventory);
                debugLog("Successfully made a backup of the current inventory");
            }
            catch (Exception ex)
            {
                Debug.printWarning("Could not save an inventory backup!");
                Debug.printWarning("Full exception: " + ex);
            }

            try
            {
                await File.WriteAllBytesAsync(Path.Combine(invAppdataFolder, "inventory.bin"), inventory);
                debugLog("Successfully saved inventory");
            }
            catch (Exception ex) 
            {
                Debug.printWarning("Could not save changes to inventory locally! (Your recent changes might not be retained)");
                Debug.printWarning("Full exception: " + ex);
            }
        }

        public static void changePlayerLvl()
        {
            var current_items = (Object[])((Dictionary<object, object?>)((Dictionary<object, object?>)inventoryDict["body"])["response"])["current_items"];

            var profileDict = findItemBySlugName("Profile", current_items);

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
                Debug.printWarning("Original invenory is null! Cannot sync inventory!");
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

        public static void updateInventoryToNewVersion(Dictionary<object, object?> oldInv, Dictionary<object, object?> newInv)
        {
            var oldInvItems = (Object[])((Dictionary<object, object?>)((Dictionary<object, object?>)oldInv["body"])["response"])["current_items"];
            var newInvItems = (Object[])((Dictionary<object, object?>)((Dictionary<object, object?>)newInv["body"])["response"])["current_items"];

            for (int i = 0; i < oldInvItems.Length; i++)
            {
                var oldItemDict = (Dictionary<object, object?>)oldInvItems[i];

                if (oldItemDict.TryGetValue("item_slug", out object slugName))
                {
                    if (slugName.Equals("MapMode"))
                    {
                        debugLog("Skipping MapMode settings...");
                        continue;
                    }

                    //Check if it's favourited
                    bool isFavourited = getIsFavouriteValue(oldItemDict);
                    if (isFavourited)
                    {
                        debugLog(String.Format("Item: {0} is favourited! Updating in new inv...", (string)slugName));

                        var newItemDict = findItemBySlugName((string)slugName, newInvItems);

                        ((Dictionary<object, object?>)newItemDict["data"])["bIsFavorite"] = isFavourited;
                    }

                    //Check if there are equipped items
                    if (hasEquippedItems(oldItemDict))
                    {
                        var oldEquippedItemsDict = getEquippedItemsDict(oldItemDict);

                        var newItemDict = findItemBySlugName((string)slugName, newInvItems);

                        var newEquippeditemsDict = getEquippedItemsDict(newItemDict);

                        foreach (var itemType in oldEquippedItemsDict)
                        {
                            //------------------
                            //Update item itself
                            string oldEquippedItemId = (string)((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)oldEquippedItemsDict[itemType.Key])["items"])[0])["uniqueId"];
                            debugLog("Old equipped item ID: " + oldEquippedItemId);

                            var oldEquippedItemDict = findItemById(oldEquippedItemId, oldInvItems);

                            //Use slug name if it's not found by ID
                            if (oldEquippedItemDict == null)
                            {
                                oldEquippedItemDict = findItemBySlugName(oldEquippedItemId, oldInvItems);
                            }

                            //If still null, skip item
                            if (oldEquippedItemDict == null)
                            {
                                printWarning("Equipped item could not be found! Skipping...");
                                continue;
                            }

                            //Get slug name
                            string oldEquippedItemSlug = (string)oldEquippedItemDict["item_slug"];
                            debugLog("Old equipped item slug name: " + oldEquippedItemSlug);

                            //Find new item
                            var newItemItemToEquipId = (string)findItemBySlugName(oldEquippedItemSlug, newInvItems)["id"];
                            debugLog("New item to equip ID: " + newItemItemToEquipId);

                            ((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)newEquippeditemsDict[itemType.Key])["items"])[0])["uniqueId"] = newItemItemToEquipId;
                            debugLog(String.Format("Set value for {0} to: {1} ", (string)itemType.Key, newItemItemToEquipId));


                            //--------------------
                            //Update randomize type
                            debugLog("Type of randomizeType: " + ((Dictionary<object, object?>)oldEquippedItemsDict[itemType.Key])["randomizeType"].GetType());
                            var oldRandomizeType = ((Dictionary<object, object?>)oldEquippedItemsDict[itemType.Key])["randomizeType"];

                            ((Dictionary<object, object?>)newEquippeditemsDict[itemType.Key])["randomizeType"] = oldRandomizeType;
                            debugLog("Set new randomizeType to: " + ((Dictionary<object, object?>)newEquippeditemsDict[itemType.Key])["randomizeType"]);
                        }
                    }
                }
            }

            debugLog("Successfully updated items!");
        }

        public static void randomizeThings()
        {
            var body = (Dictionary<object, object?>)inventoryDict["body"];

            var current_items = (Object[])((Dictionary<object, object?>)body["response"])["current_items"];

            randomizeAccountId(current_items);
            //randomizeInventoryIds();
            randomizeMapModeIds(current_items);
            saveChanges();

        }

        private static void randomizeMapModeIds(Object[] current_items)
        {
            var mapMode = findItemBySlugName("MapMode", current_items);

            if (mapMode == null) return;

            var mapmodeSlots = getEquippedItemsDict(mapMode);

            //Set ID in slots
            debugLog("Current ID: " + (string)((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)mapmodeSlots["Character"])["items"])[0])["uniqueId"]);
            ((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)mapmodeSlots["Character"])["items"])[0])["uniqueId"] = generateRandomId();
            debugLog("Set MapMode character uniqueId to: " + ((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)mapmodeSlots["Character"])["items"])[0])["uniqueId"]);

            var characterLoadouts = (Dictionary<object, object?>)((Dictionary<object, object?>)mapMode["data"])["characterLoadouts"];

            //Randomize IDs for each character
            foreach (var character in characterLoadouts)
            {
                var items = (Dictionary<object, object?>)((Dictionary<object, object?>)((Dictionary<object, object?>)character.Value)["slots"])["slots"];

                foreach (var item in items)
                {
                    debugLog("Current ID: " + (string)((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)item.Value)["items"])[0])["uniqueId"]);
                    ((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)item.Value)["items"])[0])["uniqueId"] = generateRandomId();
                    debugLog(String.Format("Set MapMode character: {0} uniqueId to: {1} ", item.Key, (string)((Dictionary<object, object?>)((Object[])((Dictionary<object, object?>)item.Value)["items"])[0])["uniqueId"]));
                }
            }

            debugLog("Successfully randomized MapMode IDs!");
        }

        private static void randomizeAccountId(Object[] current_items)
        {
            var body = (Dictionary<object, object?>)inventoryDict["body"];

            //Randomize GUID
            ((Dictionary<object, object?>)body["transaction"])["transaction_id"] = Guid.NewGuid().ToString();

            debugLog("New GUID: " + ((Dictionary<object, object?>)body["transaction"])["transaction_id"]);

            body["account_id"] = accountId;

            for (int i = 0; i < current_items.Length; i++)
            {
                if (((Dictionary<object, object?>)current_items[i]).TryGetValue("account_id", out var validAccountId))
                {
                    ((Dictionary<object, object?>)current_items[i])["account_id"] = accountId;
                }
            }

            debugLog("Account ID randomized successfully to: " + accountId);
        }

        private static void randomizeInventoryIds(Object[] current_items)
        {
            for (int i = 0; i < current_items.Length; i++)
            {
                if (((Dictionary<object, object?>)current_items[i]).TryGetValue("id", out var validId))
                {
                    ((Dictionary<object, object?>)current_items[i])["id"] = InvHelpers.generateRandomId();
                    debugLog("New itemID: " + ((Dictionary<object, object?>)current_items[i])["id"]);
                }
            }

            debugLog("Inventory IDs randomized successfully!");
        }
    }
}
