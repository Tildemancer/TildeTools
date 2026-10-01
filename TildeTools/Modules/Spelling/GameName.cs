namespace TildeTools.Modules.Spelling;

// In rank order: a word that's part of several names says the highest.
internal enum NameKind { Other, Creator, Place, Npc }

// Full: the whole name a word is part of, "Wuk Lamat" for "Wuk", or for the name generator, which of its lists
// Others: how many more whole names the word is part of

// A 'seen' counter; how often the game's text shows a name no sheet gives, capitalized or not...
internal readonly record struct GameName(NameKind Kind, string Full, int Others = 0, int Seen = 0);
