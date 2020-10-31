# Discord Channel Exporter
Exports channel content and downloads attachments from Discord guilds.

## Setup
1. Clone the repo into a directory
2. Open the solution in Visual Studio (Community 2019 Version 16.7.6 was used) and build the solution.
	- (optional) Copy the built application to somewhere with a lot of storage (depending on how big your server is)
3. Open `config.json` and configure to your liking.
4. Ensure the bot is added to the server you want to export channels from.
5. Open `DiscordBot.exe`, ensure there are no errors and wait until the gateway is connected before running any commands.

## Usage
### Commands
* `[prefix] update` - Update the channel cache manually for the channel it is ran in.
* `[prefix] export <includeStatistics (true/false)> <channel snowflakes...>` - Update and export the specified channels (or the current channel where none were specified)

## Channel Cache
The content (messages, embeds and attachments) of channels is cached and changes to messages that are older than the last exported message will **not** be tracked. It is therefore recommended that you lock the channels you are exporting and hide them from view of regular users to stop messages from being edited/deleted while the process is taking place.

## Exports
To ensure that a channel caching operation can pick up where it left off, all have their own JSON file for  messages. When backing-up multiple channels where many of the author's username's and attachments are shared across them, it makes sense to only store this information once.

### Data Formats

#### Attachment "Pointers"
Storing the entire URL of an attachment as `https://cdn.discordapp.com/attachments/771982307170058270/772001142455533578/vFvNhBa.png` is not space efficient therefore, the URL for a CDN attachment is stored once for each channel in its channel cache and wherever that URL is referenced, it is replaced with the following: `!attachment-snowflake/filename.extension` (or for the example above: `!772001142455533578/vFvNhBa.png`)

#### Channel Message Cache
Located at (by default) `channelcache/[channel-snowflake].json`, this file stores the constantly updating *cache* of a channel.
```json
{
	"channel-snowflake": 771982307170058270, // The snowflake / ID of the channel which this file pertains to
	"newest-snowflake": 772001142938533898,  // *usually* the most recent message that has been cached
	
	// A mapping of Discord ID -> Discord Username of all authors who have posted in this channel
	"authors": {
		"140378625726742528": "Jaymo#1337"
	},

	// A mapping of attachment ID -> URL of all attachments (with "pointers") in this channel
	"attachments": {
		"772001142455533578": "https://cdn.discordapp.com/attachments/771982307170058270/772001142455533578/vFvNhBa.png"
	},

	// A dictionary which stores all messages in this channel where the key is the message ID and value is message data
	"messages": {
		"772001142938533898": {
			"author": 140378625726742528, // The discord ID / snowflake of the author of this message
			"content": "",                // The text content of this message
			"attachments": [              // An array of attachment URLs or "pointers" included in this message
				"!772001142455533578/vFvNhBa.png"
			],
			"embeds": null,               // An array of embeds included with this message
			"pinned": null                // True if this message was pinned in this channel when it was cached
		},
		"771982360194842645": {
			"author": 140378625726742528,
			"content": "hi channel 1",
			"attachments": null,
			"embeds": null,
			"pinned": null
		}
	}
}
```

#### Export
To be even more space efficient for an export bundle of multiple channels, the data of all channels in a bundle is collated and organised in a way which takes up the least amount of space possible.
```json
{
	// null and default values are ignored on serialization but are displayed in this example for clarity.
	// Additionally, these counters are only included when specified as this data is redundant.
	// Nice for quickly seeing export statistics though :D
	"channelCount": 2,
	"messageCount": 2,
	"authorCount": 1,
	"uniqueAttachmentCount": 1,

	// n -> names: Stores the friendly name associated with a Discord snowflake (where applicable)
	"n": {
		// For a user, the this is their *cached* Discord username + discriminator
		"140378625726742528": "Jaymo#1337",

		// For a channel, this is the channel name at the time of export
		"771982307170058270": "channel-one",
		"771982333275275274": "channel-two"
	},

	// d -> data: stores the channel data in the following format:
	// [channel1-snowflake]
	// ├────[author1-snowflake]
	// │    ├────[message1-snowflake]
	// │    ├────[message2-snowflake]
	// │    └────[message3-snowflake]
	// └────[author2-snowflake]
	//      ├────[message4-snowflake]
	//      ├────[message5-snowflake]
	//      └────[message6-snowflake]
	// ...
	"d": {
		"771982307170058270": {
			"140378625726742528": {
				"771982360194842645": {
					"c": "hi channel 1", // c -> content: for a message with text, this is the message text
					"a": null,           // a -> attachments: for messages that have attachments, this is an array of URLs OR "attachment pointers"
					"e": null,           // e -> embeds: for messages which have embeds, this is an array of embed data which is *mostly* kept intact from how the Discord API presents it
					"p": null            // p -> pinned: is true when this message is pinned in this channel
				},
				"772001142938533898": {
					"c": "", 
					"a": [
						// attachment pointers keep the attachment ID, original file name and extension but point to a file in export directory (or content cache)
						"!772001142455533578/vFvNhBa.png"
					],
					"e": null,
					"p": null
				}
			}
		},
		"771982333275275274": {
			"140378625726742528": {
				"771982374534774784": {
					"c": "hi channel 2",
					"a": null,
					"e": null,
					"p": null
				}
			}
		},
	}
}
```