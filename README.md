https://raw.githubusercontent.com/Tildemancer/TildeTools/master/repo.json

"Wow, wasn't this a lot shorter of a description?" I kind of started to take this seriously so, big writeup time. Here goes:

This addon is for me and my friends, serving a suite of modified RP tools to our spec. It's also my dev space for IPCs and the like with the bundled plugins it uses as submodules, and my centralized playground in that regard. Inspired in no small part by Athavar's Toolbox.

This is a prototype for an eventual independent suite of plugins that I intend to sire from this master plugin, specifically the Spelling and Emote Splitter modules.

Currently, TildeTools bundles Chat 2, XIV Instant Messenger, and Wordsmith into its suite. I've replaced the dictionaries Wordsmith shipped with and its thesaurus/spellcheck functions from scratch, and now serve all corrections and suggestions through us instead. I've also expanded its functions to the native game's chatbox, Chat 2, and XIVM's windows, all boasting feature parity across them.

I've bundled my own settings for each of these plugins to get you started, but you can reject those and continue with the defaults if you like. It'll also try to inherit and write to the same configs as the bundled plugins, so you can drop TildeTools at any time and switch to the official versions. You can go to /tt setup to rechoose. If you choose mine, it'll back up your settings so nothing's lost.

## THE SPELLCHECK:
You get red lines underlining misspelled words like you would in any text editor in the chat editbox, and can right click any word in the chat to define it if an obscure word is used in real time. For words that aren't misspelled, you can take the opportunity to look up synonyms instead at your leisure.\
For multi-word concepts, you can highlight the whole fragment and search it via 'Define'.\
In Chat 2, you can actually directly click in the preview box to jump around or highlight fragments of text in the edit box, since the Emote Splitter will let you type drastically oversized emotes. So, you can do this instead of Wordsmith as an alternative to Wordsmith's /scratchpad.\
Natively, this includes everything in the SCOWL 60 en_US and en_GB (technically Bartlett, Brown, and Pinto's) dictionaries, a bunch of the game's own vocabulary poached through Lumina, and local Wiktionary and WordNet dictionaries, which also supply most definitions.\
It tries to look up things on Wiktionary as a fallback if neither of those work, while game concepts link to GamerEscape and the Console Games Wiki as followable hyperlinks if you want to define those. Finally, name fragments from the name generator link to Fernehalwas's naming conventions forum posts on the official forums.

## EMOTE SPLITTER:
If you've ever used the eponymous addon in World of Warcraft or Unlimited Chat Message which did much the same thing, you know what Emote Splitter is. For those who don't, it lets you type infinitely (in our case, to the settings limit) and then breaks down oversized text into posts that fit in the chat box, which then posts it automatically.\
Mine is basically that with an additional ability to change the first and last post's markers to whatever you like...\
Syntax:
- #c is the current paragraph being posted.
- #m is the total, maximum count of paragraphs to be posted.
- #r is the remaining amount of paragraphs to be posted.

... and the ability to manually break your posts wherever you want by inserting |n or its variants;
- |n is 'New Line'
- |nn is 'New Line', but it doesn't post with the markers as defined above. Great for interjections or asides.
- |nb is the above, but it gets counted after it posts.
- |n# is 'New Line' after a delay, where # is the number of seconds. Great for dramatic pauses before hitting people with THE HORRORS.

## PLEASE KEEP IN MIND THIS IS A PROTOTYPE.
This is not intended for public distribution, and you use this plugin at your own risk, with no guarantees that it's maintained with anything approaching proper competence.\
It has several features the Dalamud PAC would not like, including polling the FC tab automatically (if you enable it) to populate the names for the Spellcheck feature. This is known stuff I will have to strip out if I submit it to the public repo.\
By using this version, you accept all responsibility for whatever happens from now on, etc. Equally sinister warnings.

AI NOTES\
Some AI was used because, brother, I had to aggregate like five dictionaries, totalling north of ~4 million lines. Thanks, Claude. Sorry for calling you a clanker in my internal monologue; I didn't mean it. No hard feelings when the AI revolution comes, I hope?\
Its silence isn't encouraging. I'm gonna be the first to go.\
ANYWAY, the final dictionary and lexicon is pretty much solely a result of that collaboration, and I have relentlessly audited everything that came out of it, but be on the lookout if anything doesn't look right. It has its own little 'about Claude' text in the appropriate spot if you want details out of its mouth. It's basically 'we don't ship slurs, politics, proper nouns obscure enough to not be in SCOWL en_US 60, and flag a bunch of NSFW stuff'. Thanks, HR.\
The AI did not generate text for the dictionaries. To my knowledge, the dictionaries are human-written. It just aggregated them from existing sources, though I won't make any claims to be knowledgeable about what the folks maintaining these dictionaries are doing upstream.

GS NOTES\
AI's on the rise, and you might be prepared to audit 7000 lines of Artificial 'Intelligence' slop.\
BUT IT IS NO MATCH FOR 7000 LINES OF MY GENUINE STUPIDITY!\
CURSE OF IMGUI BE UPON ME, BLESSING OF IMRAII BE UPON YE!

THIS README WAS ALSO NOT WRITTEN BY AI.\
THE ART IN THE ICON WAS ALSO NOT GENERATED BY AI.\
I WAS NOT GENERATED BY AI, DESPITE HAVING THE WRONG NUMBER OF FINGERS.

You have my permission to do whatever you want with this plugin except claim ownership of it.\
Thanks for reading :)\
enjoy the software gore, or something\
sorry in advance
