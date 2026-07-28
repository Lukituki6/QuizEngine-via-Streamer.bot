using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;


public class CPHInline
{
    // Komendy można zmienić, jeżeli kolidują z innymi komendami na czacie.
    private const string PointsCommand = "!punkty"; //te 
    private const string RankingCommand = "!ranking"; // o te
    private const string AdminCommand = "!quiz"; // i te

    private const string StateVariable = "localQuiz_state_v1";
    private const string ScoresVariable = "localQuiz_scores_v1";
    private const string ScoresImportBackupVariable = "localQuiz_scores_before_import_v1";
    private const int MaxAnswers = 12; // Maksymalna liczba odpowiedzi w quizie
    private const int MaxChatMessageLength = 470;
    private const int MaxImportedScores = 25000;
    private const int MaxImportedPayloadLength = 7500000;
    private const int MaxOperationLength = 40;
    private const int MaxQuestionLength = 500;
    private const int MaxAnswerLength = 240;
    private const int MaxAnswersPayloadLength = 20000;
    private const int MaxPartialIndicesPayloadLength = 256;
    private const int MaxUserIdLength = 160;

  
    private const bool AnnounceRoundEventsInChat = false;

    private static readonly object Gate = new object();
    private static readonly Regex VoteRegex =
        new Regex(@"^!(1[0-2]|[1-9])$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool Execute()
    {
        try
        {
            lock (Gate)
            {
                string operation = GetStringArg("quizOperation");
                if (!String.IsNullOrWhiteSpace(operation))
                {
                    operation = operation.Trim().ToLowerInvariant();
                    if (operation.Length > MaxOperationLength ||
                        operation.Any(character =>
                            character < 'a' || character > 'z'))
                    {
                        BroadcastError("Nieprawidłowa operacja panelu.");
                        return true;
                    }

                    HandlePanelOperation(operation);
                    return true;
                }

                HandleTwitchChat();
            }
        }
        catch (Exception ex)
        {
            CPH.LogError("[LocalQuiz] " + ex);
            BroadcastError("Wystąpił błąd silnika quizu. Szczegóły są w logu Streamer.bot.");
        }

        return true;
    }

    private void HandlePanelOperation(string operation)
    {
        if (operation == "sync")
        {
            BroadcastState(LoadState(), LoadScores());
            return;
        }

        if (operation == "getscores")
        {
            BroadcastScores(LoadScores());
            return;
        }

        if (operation == "start")
        {
            StartQuiz(
                GetStringArg("question"),
                GetStringArg("answersJson"),
                GetIntArg("durationSeconds", 0),
                true);
            return;
        }

        if (operation == "lock" || operation == "lockexpired")
        {
            LockQuiz(operation == "lockexpired", true);
            return;
        }

        if (operation == "reveal")
        {
            RevealAnswer(
                GetIntArg("correctIndex", 0),
                GetIntArg("pointsPerCorrect", 1),
                GetIntArg("pointsPerAdjacent", 0),
                GetStringArg("scoringMode"),
                GetStringArg("partialIndicesJson"),
                true);
            return;
        }

        if (operation == "cancel")
        {
            CancelQuiz(true);
            return;
        }

        if (operation == "hide")
        {
            SetOverlayVisibility(false);
            return;
        }

        if (operation == "show")
        {
            SetOverlayVisibility(true);
            return;
        }

        if (operation == "resetscores")
        {
            if (!String.Equals(GetStringArg("confirmText"), "RESET", StringComparison.Ordinal))
            {
                BroadcastError("Reset rankingu odrzucony: wymagane jest potwierdzenie RESET.");
                return;
            }

            Dictionary<string, ScoreEntry> emptyScores =
                new Dictionary<string, ScoreEntry>(StringComparer.OrdinalIgnoreCase);
            SaveScores(emptyScores);
            BroadcastState(LoadState(), emptyScores);
            BroadcastScores(emptyScores);
            BroadcastNotice("Ranking punktów został wyzerowany.");
            return;
        }

        if (operation == "setscore")
        {
            SetScoreFromPanel(
                GetStringArg("userId"),
                GetIntArg("newPoints", 0));
            return;
        }

        if (operation == "importscores")
        {
            ImportScoresFromPanel(
                GetStringArg("scoresJson"),
                GetStringArg("importMode"),
                GetStringArg("confirmText"));
            return;
        }

        if (operation == "restoreimportbackup")
        {
            RestoreScoresBeforeImport(GetStringArg("confirmText"));
            return;
        }

        BroadcastError("Nieznana operacja panelu: " + operation);
    }

    private void HandleTwitchChat()
    {
        string message = GetStringArg("message").Trim();
        if (String.IsNullOrEmpty(message))
        {
            return;
        }

   
        if (GetBoolArg("isInternal"))
        {
            return;
        }

        Match voteMatch = VoteRegex.Match(message);
        if (voteMatch.Success)
        {
            int answer;
            if (Int32.TryParse(voteMatch.Groups[1].Value, out answer))
            {
                RegisterVote(answer);
            }
            return;
        }

        if (String.Equals(message, PointsCommand, StringComparison.OrdinalIgnoreCase))
        {
            SendUserPoints();
            return;
        }

        if (String.Equals(message, RankingCommand, StringComparison.OrdinalIgnoreCase))
        {
            SendRanking();
            return;
        }

        bool isAdminCommand =
            message.StartsWith(AdminCommand, StringComparison.OrdinalIgnoreCase) &&
            (message.Length == AdminCommand.Length ||
             Char.IsWhiteSpace(message[AdminCommand.Length]));
        if (isAdminCommand && IsChatAdmin())
        {
            HandleAdminChatCommand(message);
        }
    }

    private void HandleAdminChatCommand(string message)
    {
        string rest = message.Length > AdminCommand.Length
            ? message.Substring(AdminCommand.Length).Trim()
            : "";
        if (String.IsNullOrWhiteSpace(rest) ||
            String.Equals(rest, "pomoc", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(rest, "help", StringComparison.OrdinalIgnoreCase))
        {
            SendChat(
                "Quiz admin: " + AdminCommand +
                " start Pytanie | Odp. 1 | Odp. 2 ... • " + AdminCommand +
                " zamknij • " + AdminCommand +
                " wynik NUMER [PEŁNE PUNKTY] [PUNKTY ZA ODPOWIEDŹ OBOK] • " +
                AdminCommand + " anuluj • " + AdminCommand + " pokaz/ukryj");
            return;
        }

        if (rest.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
        {
            string definition = rest.Substring(6).Trim();
            string[] parts = definition
                .Split(new[] { '|' }, StringSplitOptions.None)
                .Select(x => x.Trim())
                .Where(x => !String.IsNullOrWhiteSpace(x))
                .ToArray();

            if (parts.Length < 3)
            {
                SendChat(
                    "Quiz: użycie: " + AdminCommand +
                    " start Pytanie | Odpowiedź 1 | Odpowiedź 2 | ...");
                return;
            }

            string question = parts[0];
            string answersJson = JsonConvert.SerializeObject(parts.Skip(1).ToList());
            StartQuiz(question, answersJson, 0, true);
            return;
        }

        if (String.Equals(rest, "zamknij", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(rest, "stop", StringComparison.OrdinalIgnoreCase))
        {
            LockQuiz(false, true);
            return;
        }

        if (rest.StartsWith("wynik ", StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith("odpowiedz ", StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith("odpowiedź ", StringComparison.OrdinalIgnoreCase))
        {
            string[] words = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int correctIndex = 0;
            int points = 1;
            int adjacentPoints = 0;
            if (words.Length >= 2)
            {
                Int32.TryParse(words[1], out correctIndex);
            }
            if (words.Length >= 3)
            {
                Int32.TryParse(words[2], out points);
            }
            if (words.Length >= 4)
            {
                Int32.TryParse(words[3], out adjacentPoints);
            }

  
            RevealAnswer(
                correctIndex,
                points,
                adjacentPoints,
                "adjacent",
                "[]",
                true);
            return;
        }

        if (String.Equals(rest, "anuluj", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(rest, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            CancelQuiz(true);
            return;
        }

        if (String.Equals(rest, "pokaz", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(rest, "pokaż", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(rest, "show", StringComparison.OrdinalIgnoreCase))
        {
            SetOverlayVisibility(true);
            return;
        }

        if (String.Equals(rest, "ukryj", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(rest, "hide", StringComparison.OrdinalIgnoreCase))
        {
            SetOverlayVisibility(false);
            return;
        }

        SendChat("Quiz: nieznana komenda. Wpisz " + AdminCommand + " pomoc");
    }

    private void StartQuiz(string question, string answersJson, int durationSeconds, bool announce)
    {
        QuizState oldState = LoadState();
        if (oldState.Status == "open" || oldState.Status == "locked")
        {
            BroadcastError("Najpierw zakończ albo anuluj trwające pytanie.");
            return;
        }

        question = (question ?? "").Trim();
        if (question.Length < 2 || question.Length > MaxQuestionLength)
        {
            BroadcastError(
                "Pytanie musi mieć od 2 do " + MaxQuestionLength + " znaków.");
            return;
        }

        answersJson = answersJson ?? "[]";
        if (answersJson.Length > MaxAnswersPayloadLength)
        {
            BroadcastError("Lista odpowiedzi jest zbyt duża.");
            return;
        }

        List<string> answerTexts;
        try
        {
            answerTexts = JsonConvert.DeserializeObject<List<string>>(answersJson);
        }
        catch
        {
            BroadcastError("Nie udało się odczytać listy odpowiedzi.");
            return;
        }

        answerTexts = answerTexts ?? new List<string>();
        if (answerTexts.Any(x => (x ?? "").Trim().Length > MaxAnswerLength))
        {
            BroadcastError(
                "Każda odpowiedź może mieć maksymalnie " +
                MaxAnswerLength + " znaków.");
            return;
        }

        answerTexts = answerTexts
            .Select(x => (x ?? "").Trim())
            .Where(x => !String.IsNullOrWhiteSpace(x))
            .ToList();

        if (answerTexts.Count < 2)
        {
            BroadcastError("Quiz musi mieć co najmniej 2 odpowiedzi.");
            return;
        }

        if (answerTexts.Count > MaxAnswers)
        {
            BroadcastError("Maksymalna liczba odpowiedzi to " + MaxAnswers + ".");
            return;
        }

        durationSeconds = Math.Max(0, Math.Min(durationSeconds, 3600));
        long now = UnixMilliseconds();

        QuizState state = new QuizState();
        state.Status = "open";
        state.Visible = true;
        state.Question = question;
        state.Options = answerTexts
            .Select(x => new QuizOption { Text = x, Count = 0 })
            .ToList();
        state.Votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        state.CorrectIndex = 0;
        state.PointsPerCorrect = 1;
        state.PointsPerAdjacent = 0;
        state.ScoringMode = "adjacent";
        state.PartialIndices = new List<int>();
        state.PointsAwarded = false;
        state.StartedAt = now;
        state.ClosesAt = durationSeconds > 0 ? now + (durationSeconds * 1000L) : 0;
        state.UpdatedAt = now;
        state.RoundId = Guid.NewGuid().ToString("N");
        state.Revision = Math.Max(0, oldState.Revision) + 1;

        ClearVoterNames();
        SaveState(state);
        Dictionary<string, ScoreEntry> scores = LoadScores();
        BroadcastState(state, scores);

        if (announce && AnnounceRoundEventsInChat)
        {
            string timerText = durationSeconds > 0
                ? " Masz " + durationSeconds + " s."
                : "";
            SendChat(
                "Quiz: " + question + " - głosuj komendą !1–!" +
                answerTexts.Count + "." + timerText);
        }
    }

    private void RegisterVote(int answerIndex)
    {
        QuizState state = LoadState();
        if (state.Status != "open")
        {
            return;
        }

        if (state.ClosesAt > 0 && UnixMilliseconds() >= state.ClosesAt)
        {
            LockQuiz(true, true);
            return;
        }

        if (answerIndex < 1 || answerIndex > state.Options.Count)
        {
            return;
        }

        string userId = GetStringArg("userId");
        string userName = GetDisplayName();
        if (String.IsNullOrWhiteSpace(userId))
        {
            string login = GetStringArg("userName");
            if (String.IsNullOrWhiteSpace(login))
            {
                return;
            }
            userId = "name:" + login.Trim().ToLowerInvariant();
        }

        state.Votes[userId] = answerIndex;
        state.UpdatedAt = UnixMilliseconds();
        state.Revision++;
        RecalculateCounts(state);
        SaveState(state);
        BroadcastState(state, LoadScores());


        if (!String.IsNullOrWhiteSpace(userName))
        {
            Dictionary<string, string> voterNames = LoadVoterNames();
            voterNames[userId] = userName;
            SaveVoterNames(voterNames);
        }
    }

    private void LockQuiz(bool expired, bool announce)
    {
        QuizState state = LoadState();
        if (state.Status == "locked")
        {
            BroadcastState(state, LoadScores());
            return;
        }

        if (state.Status != "open")
        {
            BroadcastError("Nie ma otwartego głosowania do zamknięcia.");
            return;
        }

        state.Status = "locked";
        state.UpdatedAt = UnixMilliseconds();
        state.Revision++;
        RecalculateCounts(state);
        SaveState(state);
        BroadcastState(state, LoadScores());

        if (announce && AnnounceRoundEventsInChat)
        {
            SendChat(
                expired
                    ? "Quiz: czas minął - głosowanie zamknięte."
                    : "Quiz: głosowanie zostało zamknięte.");
        }
    }

    private void RevealAnswer(
        int correctIndex,
        int pointsPerCorrect,
        int pointsPerAdjacent,
        string scoringMode,
        string partialIndicesJson,
        bool announce)
    {
        QuizState state = LoadState();
        if (state.Status != "open" && state.Status != "locked")
        {
            BroadcastError("Nie ma pytania oczekującego na ujawnienie odpowiedzi.");
            return;
        }

        if (correctIndex < 1 || correctIndex > state.Options.Count)
        {
            BroadcastError("Wybierz poprawną odpowiedź od 1 do " + state.Options.Count + ".");
            return;
        }

        pointsPerCorrect = Math.Max(0, Math.Min(pointsPerCorrect, 1000));
    
        pointsPerAdjacent = Math.Max(
            0,
            Math.Min(pointsPerAdjacent, pointsPerCorrect));

        scoringMode = String.Equals(
            scoringMode,
            "manual",
            StringComparison.OrdinalIgnoreCase)
                ? "manual"
                : "adjacent";

        List<int> partialIndices = new List<int>();
        if (scoringMode == "manual")
        {
            partialIndicesJson = partialIndicesJson ?? "[]";
            if (partialIndicesJson.Length > MaxPartialIndicesPayloadLength)
            {
                BroadcastError("Lista odpowiedzi częściowych jest zbyt duża.");
                return;
            }

            try
            {
                partialIndices =
                    JsonConvert.DeserializeObject<List<int>>(partialIndicesJson)
                    ?? new List<int>();
            }
            catch
            {
                BroadcastError("Nie udało się odczytać ręcznie wybranych odpowiedzi.");
                return;
            }

            partialIndices = partialIndices
                .Where(x =>
                    x >= 1 &&
                    x <= state.Options.Count &&
                    x != correctIndex)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }
        else
        {
            if (correctIndex > 1)
            {
                partialIndices.Add(correctIndex - 1);
            }
            if (correctIndex < state.Options.Count)
            {
                partialIndices.Add(correctIndex + 1);
            }
        }

        if (pointsPerAdjacent <= 0)
        {
            partialIndices.Clear();
        }

        Dictionary<string, ScoreEntry> scores = LoadScores();
        Dictionary<string, string> voterNames = LoadVoterNames();
        int winners = 0;
        int partialWinners = 0;

        if (!state.PointsAwarded)
        {
            foreach (KeyValuePair<string, int> vote in state.Votes)
            {
                ScoreEntry entry;
                if (!scores.TryGetValue(vote.Key, out entry) || entry == null)
                {
                    string rememberedImportedName = voterNames.ContainsKey(vote.Key)
                        ? voterNames[vote.Key]
                        : vote.Key;
                    string matchingKey;
                    if (TryFindScoreKeyByName(
                            scores,
                            rememberedImportedName,
                            out matchingKey))
                    {
                        entry = scores[matchingKey];
                        scores.Remove(matchingKey);
                        entry.UserId = vote.Key;
                        scores[vote.Key] = entry;
                    }
                    else
                    {
                        entry = new ScoreEntry();
                        entry.UserId = vote.Key;
                        entry.UserName = rememberedImportedName;
                        scores[vote.Key] = entry;
                    }
                }

                string rememberedName;
                if (voterNames.TryGetValue(vote.Key, out rememberedName) &&
                    !String.IsNullOrWhiteSpace(rememberedName))
                {
                    entry.UserName = rememberedName;
                }

                entry.AnsweredQuestions++;
                if (vote.Value == correctIndex)
                {
                    entry.CorrectAnswers++;
                    entry.Points += pointsPerCorrect;
                    winners++;
                }
                else if (pointsPerAdjacent > 0 &&
                         partialIndices.Contains(vote.Value))
                {
                    entry.Points += pointsPerAdjacent;
                    partialWinners++;
                }
                entry.LastUpdatedAt = UnixMilliseconds();
            }

            SaveScores(scores);
            state.PointsAwarded = true;
        }

        state.Status = "revealed";
        state.Visible = true;
        state.CorrectIndex = correctIndex;
        state.PointsPerCorrect = pointsPerCorrect;
        state.PointsPerAdjacent = pointsPerAdjacent;
        state.ScoringMode = scoringMode;
        state.PartialIndices = partialIndices;
        state.UpdatedAt = UnixMilliseconds();
        state.Revision++;
        RecalculateCounts(state);
        SaveState(state);
        BroadcastState(state, scores);
        BroadcastScores(scores);

        if (announce && AnnounceRoundEventsInChat)
        {
            string correctText = state.Options[correctIndex - 1].Text;
            string pointsText = pointsPerCorrect == 1 ? "1 punkt" : pointsPerCorrect + " pkt";
            if (pointsPerAdjacent > 0 && partialIndices.Count > 0)
            {
                List<string> partialCommands = partialIndices
                    .Select(x => "!" + x)
                    .ToList();

                string partialPointsText = pointsPerAdjacent == 1
                    ? "1 punkt"
                    : pointsPerAdjacent + " pkt";
                string partialLabel = scoringMode == "manual"
                    ? "Wybrane odpowiedzi częściowe "
                    : "Odpowiedź obok ";
                SendChat(
                    "Quiz: poprawna odpowiedź to " + correctIndex + ") " +
                    correctText + ". Dokładnie trafiło " + winners +
                    " osób (+" + pointsText + "). " + partialLabel +
                    String.Join(" lub ", partialCommands) + " wybrało " +
                    partialWinners + " osób (+" + partialPointsText + ").");
            }
            else
            {
                SendChat(
                    "Quiz: poprawna odpowiedź to " + correctIndex + ") " +
                    correctText + ". Trafiło " + winners +
                    " osób - każda otrzymuje " + pointsText + "!");
            }
        }
    }

    private void CancelQuiz(bool announce)
    {
        QuizState previous = LoadState();
        QuizState empty = CreateDefaultState();
        empty.Revision = Math.Max(0, previous.Revision) + 1;
        SaveState(empty);
        ClearVoterNames();
        BroadcastState(empty, LoadScores());
    }

    private void SetOverlayVisibility(bool visible)
    {
        QuizState state = LoadState();
        if (state.Status == "idle" && visible)
        {
            BroadcastError("Nie ma pytania do pokazania.");
            return;
        }

        state.Visible = visible;
        state.UpdatedAt = UnixMilliseconds();
        state.Revision++;
        SaveState(state);
        BroadcastState(state, LoadScores());
    }

    private void SendUserPoints()
    {
        string userId = GetStringArg("userId");
        string userName = GetDisplayName();
        if (String.IsNullOrWhiteSpace(userId))
        {
            string login = GetStringArg("userName");
            userId = "name:" + (login ?? "").Trim().ToLowerInvariant();
        }

        Dictionary<string, ScoreEntry> scores = LoadScores();
        ScoreEntry entry;
        if (!scores.TryGetValue(userId, out entry) || entry == null)
        {
            string matchingKey;
            if (!TryFindScoreKeyByName(scores, userName, out matchingKey))
            {
                SendChat(userName + ": masz obecnie 0 punktów w quizie.");
                return;
            }

            entry = scores[matchingKey];
            if (!String.Equals(
                    matchingKey,
                    userId,
                    StringComparison.OrdinalIgnoreCase))
            {
                scores.Remove(matchingKey);
                entry.UserId = userId;
                entry.UserName = userName;
                scores[userId] = entry;
                SaveScores(scores);
                BroadcastState(LoadState(), scores);
                BroadcastScores(scores);
            }
        }

        SendChat(
            userName + ": " + entry.Points + " pkt • poprawne " +
            entry.CorrectAnswers + "/" + entry.AnsweredQuestions + ".");
    }

    private void SendRanking()
    {
        List<ScoreEntry> ranking = SortScores(LoadScores()).Take(5).ToList();
        if (ranking.Count == 0)
        {
            SendChat("Quiz: ranking jest jeszcze pusty.");
            return;
        }

        List<string> parts = new List<string>();
        for (int i = 0; i < ranking.Count; i++)
        {
            parts.Add(
                (i + 1) + ". " + SafeName(ranking[i].UserName) +
                " - " + ranking[i].Points + " pkt");
        }
        SendChat("Quiz TOP 5: " + String.Join(" • ", parts));
    }

    private void SetScoreFromPanel(string userId, int newPoints)
    {
        userId = (userId ?? "").Trim();
        if (String.IsNullOrEmpty(userId))
        {
            BroadcastError("Nie wybrano użytkownika do korekty.");
            return;
        }
        if (userId.Length > MaxUserIdLength ||
            userId.Any(character => Char.IsControl(character)))
        {
            BroadcastError("Nieprawidłowe ID użytkownika.");
            return;
        }

        Dictionary<string, ScoreEntry> scores = LoadScores();
        ScoreEntry entry;
        if (!scores.TryGetValue(userId, out entry) || entry == null)
        {
            BroadcastError("Nie znaleziono tego użytkownika w rankingu.");
            return;
        }

        entry.Points = Math.Max(0, Math.Min(newPoints, 100000000));
        entry.LastUpdatedAt = UnixMilliseconds();
        SaveScores(scores);
        BroadcastState(LoadState(), scores);
        BroadcastScores(scores);
        BroadcastNotice("Zapisano korektę punktów dla " + SafeName(entry.UserName) + ".");
    }

    private void ImportScoresFromPanel(
        string scoresJson,
        string importMode,
        string confirmText)
    {
        scoresJson = scoresJson ?? "";
        if (String.IsNullOrWhiteSpace(scoresJson))
        {
            BroadcastError("Plik importu nie zawiera żadnych wpisów rankingu.");
            return;
        }

        if (scoresJson.Length > MaxImportedPayloadLength)
        {
            BroadcastError("Plik importu jest zbyt duży.");
            return;
        }

        string mode = String.Equals(
            importMode,
            "merge",
            StringComparison.OrdinalIgnoreCase)
                ? "merge"
                : "replace";
        string requiredConfirmation =
            mode == "merge" ? "IMPORT_MERGE" : "IMPORT_REPLACE";
        if (!String.Equals(
                confirmText,
                requiredConfirmation,
                StringComparison.Ordinal))
        {
            BroadcastError("Import rankingu został odrzucony: brak potwierdzenia.");
            return;
        }

        List<ImportedScoreEntry> importedRows;
        try
        {
            importedRows =
                JsonConvert.DeserializeObject<List<ImportedScoreEntry>>(scoresJson);
        }
        catch (Exception ex)
        {
            CPH.LogWarn("[LocalQuiz] Nieprawidłowy JSON importu: " + ex.Message);
            BroadcastError("Nie udało się odczytać danych z pliku CSV.");
            return;
        }

        if (importedRows == null || importedRows.Count == 0)
        {
            BroadcastError("Plik importu nie zawiera żadnych wpisów rankingu.");
            return;
        }

        if (importedRows.Count > MaxImportedScores)
        {
            BroadcastError(
                "Plik zawiera zbyt wiele wpisów. Maksimum jednego importu to " +
                MaxImportedScores + ".");
            return;
        }

        long now = UnixMilliseconds();
        List<PreparedImportedScore> preparedRows =
            new List<PreparedImportedScore>();
        HashSet<string> importedIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> importedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < importedRows.Count; index++)
        {
            ImportedScoreEntry source = importedRows[index];
            int rowNumber = index + 2;
            if (source == null)
            {
                BroadcastError("Wiersz " + rowNumber + " importu jest pusty.");
                return;
            }

            string userName = (source.UserName ?? "")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (String.IsNullOrWhiteSpace(userName))
            {
                BroadcastError(
                    "Wiersz " + rowNumber + ": brakuje nazwy użytkownika.");
                return;
            }
            if (userName.Length > 80 ||
                userName.Any(character => Char.IsControl(character)) ||
                HasSpreadsheetFormulaPrefix(userName))
            {
                BroadcastError(
                    "Wiersz " + rowNumber + ": nazwa użytkownika jest nieprawidłowa.");
                return;
            }

            string normalizedName = NormalizeScoreName(userName);
            if (!importedNames.Add(normalizedName))
            {
                BroadcastError(
                    "Plik zawiera więcej niż jeden wpis użytkownika " +
                    SafeName(userName) + ".");
                return;
            }

            string userId = (source.UserId ?? "").Trim();
            bool hasExplicitUserId = !String.IsNullOrWhiteSpace(userId);
            if (hasExplicitUserId)
            {
                if (userId.Length > MaxUserIdLength ||
                    userId.Any(character => Char.IsControl(character)) ||
                    HasSpreadsheetFormulaPrefix(userId))
                {
                    BroadcastError(
                        "Wiersz " + rowNumber + ": ID użytkownika jest nieprawidłowe.");
                    return;
                }
            }
            else
            {
                userId = BuildNameScoreId(userName);
            }

            if (!importedIds.Add(userId))
            {
                BroadcastError(
                    "Plik zawiera zduplikowane ID użytkownika w wierszu " +
                    rowNumber + ".");
                return;
            }

            if (source.Points < 0 || source.Points > 100000000 ||
                source.CorrectAnswers < 0 ||
                source.CorrectAnswers > 100000000 ||
                source.AnsweredQuestions < 0 ||
                source.AnsweredQuestions > 100000000)
            {
                BroadcastError(
                    "Wiersz " + rowNumber +
                    ": statystyki muszą być nieujemnymi liczbami całkowitymi.");
                return;
            }

            if (source.CorrectAnswers > source.AnsweredQuestions)
            {
                BroadcastError(
                    "Wiersz " + rowNumber +
                    ": liczba poprawnych nie może przekraczać odpowiedzianych.");
                return;
            }

            ScoreEntry entry = new ScoreEntry();
            entry.UserId = userId;
            entry.UserName = userName;
            entry.Points = source.Points;
            entry.CorrectAnswers = source.CorrectAnswers;
            entry.AnsweredQuestions = source.AnsweredQuestions;
            entry.LastUpdatedAt =
                source.LastUpdatedAt > 0 && source.LastUpdatedAt <= now + 86400000L
                    ? source.LastUpdatedAt
                    : now;

            PreparedImportedScore prepared = new PreparedImportedScore();
            prepared.Entry = entry;
            prepared.HasExplicitUserId = hasExplicitUserId;
            preparedRows.Add(prepared);
        }

        Dictionary<string, ScoreEntry> previousScores = LoadScores();
        Dictionary<string, ScoreEntry> result;

        if (mode == "merge")
        {
            result = new Dictionary<string, ScoreEntry>(
                previousScores,
                StringComparer.OrdinalIgnoreCase);

            foreach (PreparedImportedScore prepared in preparedRows)
            {
                ScoreEntry imported = prepared.Entry;
                string targetKey = imported.UserId;
                string matchingKey;
                if (!result.ContainsKey(targetKey) &&
                    TryFindScoreKeyByName(
                        result,
                        imported.UserName,
                        out matchingKey))
                {
                    ScoreEntry existing = result[matchingKey];
                    if (!prepared.HasExplicitUserId &&
                        existing != null &&
                        !String.IsNullOrWhiteSpace(existing.UserId))
                    {
                        targetKey = matchingKey;
                        imported.UserId = existing.UserId;
                    }
                    else if (!String.Equals(
                                 matchingKey,
                                 targetKey,
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        result.Remove(matchingKey);
                    }
                }

                result[targetKey] = imported;
            }
        }
        else
        {
            result =
                new Dictionary<string, ScoreEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (PreparedImportedScore prepared in preparedRows)
            {
                result[prepared.Entry.UserId] = prepared.Entry;
            }
        }

        CPH.SetGlobalVar(
            ScoresImportBackupVariable,
            JsonConvert.SerializeObject(previousScores),
            true);
        SaveScores(result);
        BroadcastState(LoadState(), result);
        BroadcastScores(result);
        BroadcastNotice(
            "Zaimportowano " + preparedRows.Count + " wpisów. " +
            (mode == "merge"
                ? "Ranking został połączony."
                : "Ranking został zastąpiony."));
    }

    private void RestoreScoresBeforeImport(string confirmText)
    {
        if (!String.Equals(confirmText, "COFNIJ", StringComparison.Ordinal))
        {
            BroadcastError("Przywracanie odrzucone: wymagane jest potwierdzenie COFNIJ.");
            return;
        }

        string backupJson =
            CPH.GetGlobalVar<string>(ScoresImportBackupVariable, true);
        if (String.IsNullOrWhiteSpace(backupJson))
        {
            BroadcastError("Nie znaleziono kopii rankingu sprzed importu.");
            return;
        }

        try
        {
            Dictionary<string, ScoreEntry> backup =
                JsonConvert.DeserializeObject<Dictionary<string, ScoreEntry>>(
                    backupJson);
            if (backup == null)
            {
                BroadcastError("Kopia rankingu sprzed importu jest nieprawidłowa.");
                return;
            }

            backup = new Dictionary<string, ScoreEntry>(
                backup,
                StringComparer.OrdinalIgnoreCase);
            SaveScores(backup);
            BroadcastState(LoadState(), backup);
            BroadcastScores(backup);
            BroadcastNotice("Przywrócono ranking sprzed ostatniego importu.");
        }
        catch (Exception ex)
        {
            CPH.LogWarn(
                "[LocalQuiz] Nie udało się przywrócić kopii importu: " +
                ex.Message);
            BroadcastError("Nie udało się przywrócić rankingu sprzed importu.");
        }
    }

    private string NormalizeScoreName(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return Regex.Replace(value, @"\s+", " ");
    }

    private bool HasSpreadsheetFormulaPrefix(string value)
    {
        value = (value ?? "").TrimStart();
        if (value.Length == 0)
        {
            return false;
        }

        char first = value[0];
        return first == '=' || first == '+' || first == '-' || first == '@';
    }

    private string BuildNameScoreId(string userName)
    {
        return "name:" + NormalizeScoreName(userName);
    }

    private bool TryFindScoreKeyByName(
        Dictionary<string, ScoreEntry> scores,
        string userName,
        out string matchingKey)
    {
        matchingKey = null;
        string normalizedName = NormalizeScoreName(userName);
        if (String.IsNullOrWhiteSpace(normalizedName))
        {
            return false;
        }

        foreach (KeyValuePair<string, ScoreEntry> item in scores)
        {
            if (item.Value != null &&
                String.Equals(
                    NormalizeScoreName(item.Value.UserName),
                    normalizedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                matchingKey = item.Key;
                return true;
            }
        }
        return false;
    }

    private bool IsChatAdmin()
    {
        if (GetBoolArg("isBroadcaster") ||
            GetBoolArg("isModerator") ||
            GetBoolArg("isMod"))
        {
            return true;
        }

        string userId = GetStringArg("userId");
        string broadcasterId = GetStringArg("broadcastUserId");
        return !String.IsNullOrWhiteSpace(userId) &&
               !String.IsNullOrWhiteSpace(broadcasterId) &&
               String.Equals(userId, broadcasterId, StringComparison.OrdinalIgnoreCase);
    }

    private string GetDisplayName()
    {
        string displayName = GetStringArg("user");
        if (String.IsNullOrWhiteSpace(displayName))
        {
            displayName = GetStringArg("userName");
        }
        return SafeName(displayName);
    }

    private string GetStringArg(string name)
    {
        string value;
        if (CPH.TryGetArg(name, out value) && value != null)
        {
            return value;
        }

        object raw;
        if (CPH.TryGetArg(name, out raw) && raw != null)
        {
            return Convert.ToString(raw) ?? "";
        }
        return "";
    }

    private int GetIntArg(string name, int fallback)
    {
        int value;
        if (CPH.TryGetArg(name, out value))
        {
            return value;
        }

        string text = GetStringArg(name);
        return Int32.TryParse(text, out value) ? value : fallback;
    }

    private bool GetBoolArg(string name)
    {
        bool value;
        if (CPH.TryGetArg(name, out value))
        {
            return value;
        }

        string text = GetStringArg(name);
        return Boolean.TryParse(text, out value) && value;
    }

    private QuizState LoadState()
    {
        string json = CPH.GetGlobalVar<string>(StateVariable, false);
        if (String.IsNullOrWhiteSpace(json))
        {
            return CreateDefaultState();
        }

        try
        {
            QuizState state = JsonConvert.DeserializeObject<QuizState>(json);
            if (state == null)
            {
                return CreateDefaultState();
            }

            state.Options = state.Options ?? new List<QuizOption>();
            state.Votes = state.Votes != null
                ? new Dictionary<string, int>(state.Votes, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            state.Status = String.IsNullOrWhiteSpace(state.Status) ? "idle" : state.Status;
            state.ScoringMode = String.Equals(
                state.ScoringMode,
                "manual",
                StringComparison.OrdinalIgnoreCase)
                    ? "manual"
                    : "adjacent";
            state.PartialIndices = (state.PartialIndices ?? new List<int>())
                .Where(x =>
                    x >= 1 &&
                    x <= state.Options.Count &&
                    x != state.CorrectIndex)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

       
            if (state.Status == "revealed" &&
                state.PointsPerAdjacent > 0 &&
                state.ScoringMode == "adjacent" &&
                state.PartialIndices.Count == 0)
            {
                if (state.CorrectIndex > 1)
                {
                    state.PartialIndices.Add(state.CorrectIndex - 1);
                }
                if (state.CorrectIndex < state.Options.Count)
                {
                    state.PartialIndices.Add(state.CorrectIndex + 1);
                }
            }
            return state;
        }
        catch (Exception ex)
        {
            CPH.LogError("[LocalQuiz] Nie udało się odczytać stanu: " + ex.Message);
            return CreateDefaultState();
        }
    }

    private void SaveState(QuizState state)
    {
        CPH.SetGlobalVar(
            StateVariable,
            JsonConvert.SerializeObject(state),
            false);
    }

    private Dictionary<string, ScoreEntry> LoadScores()
    {
        string json = CPH.GetGlobalVar<string>(ScoresVariable, true);
        if (String.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, ScoreEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Dictionary<string, ScoreEntry> scores =
                JsonConvert.DeserializeObject<Dictionary<string, ScoreEntry>>(json);
            return scores != null
                ? new Dictionary<string, ScoreEntry>(scores, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ScoreEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            CPH.LogError("[LocalQuiz] Nie udało się odczytać rankingu: " + ex.Message);
            return new Dictionary<string, ScoreEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveScores(Dictionary<string, ScoreEntry> scores)
    {
        CPH.SetGlobalVar(
            ScoresVariable,
            JsonConvert.SerializeObject(scores),
            true);
    }

    private Dictionary<string, string> LoadVoterNames()
    {
        const string key = "localQuiz_voterNames_v1";
        string json = CPH.GetGlobalVar<string>(key, false);
        if (String.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Dictionary<string, string> names =
                JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            return names != null
                ? new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveVoterNames(Dictionary<string, string> names)
    {
        CPH.SetGlobalVar(
            "localQuiz_voterNames_v1",
            JsonConvert.SerializeObject(names),
            false);
    }

    private void ClearVoterNames()
    {
        CPH.SetGlobalVar("localQuiz_voterNames_v1", "{}", false);
    }

    private QuizState CreateDefaultState()
    {
        QuizState state = new QuizState();
        state.Status = "idle";
        state.Visible = false;
        state.Question = "";
        state.Options = new List<QuizOption>();
        state.Votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        state.ScoringMode = "adjacent";
        state.PartialIndices = new List<int>();
        state.RoundId = "";
        state.UpdatedAt = UnixMilliseconds();
        return state;
    }

    private void RecalculateCounts(QuizState state)
    {
        foreach (QuizOption option in state.Options)
        {
            option.Count = 0;
        }

        foreach (int choice in state.Votes.Values)
        {
            if (choice >= 1 && choice <= state.Options.Count)
            {
                state.Options[choice - 1].Count++;
            }
        }
    }

    private List<ScoreEntry> SortScores(Dictionary<string, ScoreEntry> scores)
    {
        return scores.Values
            .Where(x => x != null)
            .OrderByDescending(x => x.Points)
            .ThenByDescending(x => x.CorrectAnswers)
            .ThenBy(x => x.AnsweredQuestions)
            .ThenBy(x => x.UserName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void BroadcastState(QuizState state, Dictionary<string, ScoreEntry> scores)
    {
        RecalculateCounts(state);
        int totalVotes = state.Votes.Count;
        List<ScoreEntry> ranking = SortScores(scores).Take(10).ToList();

        object payload = new
        {
            type = "quiz-state",
            version = 1,
            revision = state.Revision,
            status = state.Status,
            visible = state.Visible,
            question = state.Question,
            options = state.Options.Select((x, index) => new
            {
                index = index + 1,
                text = x.Text,
                count = x.Count
            }).ToList(),
            totalVotes = totalVotes,
            correctIndex = state.Status == "revealed" ? state.CorrectIndex : 0,
            pointsPerCorrect = state.PointsPerCorrect,
            pointsPerAdjacent = state.PointsPerAdjacent,
            scoringMode = state.ScoringMode,
            partialIndices = state.Status == "revealed"
                ? state.PartialIndices
                : new List<int>(),
            startedAt = state.StartedAt,
            closesAt = state.ClosesAt,
            roundId = state.RoundId,
            leaderboard = ranking.Select((x, index) => new
            {
                position = index + 1,
                userId = x.UserId,
                userName = SafeName(x.UserName),
                points = x.Points,
                correct = x.CorrectAnswers,
                answered = x.AnsweredQuestions
            }).ToList()
        };

        CPH.WebsocketBroadcastJson(JsonConvert.SerializeObject(payload));
    }

    private void BroadcastScores(Dictionary<string, ScoreEntry> scores)
    {
        List<ScoreEntry> ranking = SortScores(scores);
        object payload = new
        {
            type = "quiz-scores",
            version = 1,
            scores = ranking.Select((x, index) => new
            {
                position = index + 1,
                userId = x.UserId,
                userName = SafeName(x.UserName),
                points = x.Points,
                correct = x.CorrectAnswers,
                answered = x.AnsweredQuestions,
                lastUpdatedAt = x.LastUpdatedAt
            }).ToList()
        };
        CPH.WebsocketBroadcastJson(JsonConvert.SerializeObject(payload));
    }

    private void BroadcastError(string message)
    {
        object payload = new
        {
            type = "quiz-error",
            message = message
        };
        CPH.WebsocketBroadcastJson(JsonConvert.SerializeObject(payload));
        CPH.LogWarn("[LocalQuiz] " + message);
    }

    private void BroadcastNotice(string message)
    {
        object payload = new
        {
            type = "quiz-notice",
            message = message
        };
        CPH.WebsocketBroadcastJson(JsonConvert.SerializeObject(payload));
    }

    private void SendChat(string message)
    {
        message = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (message.Length > MaxChatMessageLength)
        {
            message = message.Substring(0, MaxChatMessageLength - 1) + "…";
        }
        CPH.SendMessage(message, true, true);
    }

    private string SafeName(string value)
    {
        value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return String.IsNullOrWhiteSpace(value) ? "Widz" : value;
    }

    private long UnixMilliseconds()
    {
        return (long)(DateTime.UtcNow -
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }

    public class QuizState
    {
        public string Status { get; set; }
        public bool Visible { get; set; }
        public string Question { get; set; }
        public List<QuizOption> Options { get; set; }
        public Dictionary<string, int> Votes { get; set; }
        public int CorrectIndex { get; set; }
        public int PointsPerCorrect { get; set; }
        public int PointsPerAdjacent { get; set; }
        public string ScoringMode { get; set; }
        public List<int> PartialIndices { get; set; }
        public bool PointsAwarded { get; set; }
        public long StartedAt { get; set; }
        public long ClosesAt { get; set; }
        public long UpdatedAt { get; set; }
        public string RoundId { get; set; }
        public int Revision { get; set; }
    }

    public class QuizOption
    {
        public string Text { get; set; }
        public int Count { get; set; }
    }

    public class ScoreEntry
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public int Points { get; set; }
        public int CorrectAnswers { get; set; }
        public int AnsweredQuestions { get; set; }
        public long LastUpdatedAt { get; set; }
    }

    public class ImportedScoreEntry
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public int Points { get; set; }
        public int CorrectAnswers { get; set; }
        public int AnsweredQuestions { get; set; }
        public long LastUpdatedAt { get; set; }
    }

    private class PreparedImportedScore
    {
        public ScoreEntry Entry { get; set; }
        public bool HasExplicitUserId { get; set; }
    }
}
