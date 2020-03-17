using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Storage.Domain.DTOs;
using Storage.Domain.Models;
using Storage.Infrastructure;
using Serilog;

namespace StorageService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GameController : Controller
    {
        private readonly RepositoryWrapper _repositoryWrapper;
        private readonly IMapper _mapper;


        public GameController(RepositoryWrapper repositoryWrapper, IMapper mapper)
        {
            _repositoryWrapper = repositoryWrapper;
            _mapper = mapper;
        }

        // GET the most recent game record
        public IActionResult Index()
        {
            var games = _repositoryWrapper.GameRepository.GetListQueryable
                .Include(g => g.TeamGameSummaries)
                .ThenInclude(t => t.Team)
                .OrderByDescending(g => g.Id)
                .Where(g => g.State == GameState.Created);
            Log.Information("Game index requested. Returned {0} games", games.Count());
            return Ok(games);
        }


        [HttpGet("list/{page:int}")]
        public IActionResult GetPageList(int page = 1)
        {
            if (page < 1)
            {
                Log.Error("Bad request on GameGetPageList. Requested page <1.");
                return BadRequest();
            }

            var list = _repositoryWrapper
                .GameRepository
                .GetListQueryable
                .Include(x => x.TeamGameSummaries)
                .Skip((page - 1) * 10)
                .Take(10);
            Log.Information("Returned game page {0}.", page);
            return Ok(list);
        }

        [HttpPost("create")]
        public IActionResult SetUp(SetUpGameDTO game)
        {
            Log.Information("game creation request started. Date = {0}, FirstTeam = {1}, SecondTeam = {2}, Duration = {3}", game.Date, game.FirstTeamCode, game.SecondTeamCode, game.DurationMinutes);
            var date = game.Date;
            TimeSpan timeFromNow = date - DateTime.Now;
            if (timeFromNow.TotalMinutes < 1)
            {
                Log.Error("Invalid game creartion request: Duration<1.");
                return BadRequest();
            }

            Team firstTeam = _repositoryWrapper.TeamRepository.FindByCode(game.FirstTeamCode);
            if (firstTeam == null)
            {
                Log.Error("Invalid game creartion request: FirstTeam {0} not found.", game.FirstTeamCode);
                return BadRequest();
            }

            Team secondTeam = _repositoryWrapper.TeamRepository.FindByCode(game.SecondTeamCode);
            if (secondTeam == null)
            {
                Log.Error("Invalid game creartion request: SecondTeam {0} not found.", game.SecondTeamCode);
                return BadRequest();
            }

            TeamGameSummary firstTeamGameSummary = _repositoryWrapper.TeamGameSummaryRepository.CreateNew(firstTeam);
            TeamGameSummary secondTeamGameSummary = _repositoryWrapper.TeamGameSummaryRepository.CreateNew(secondTeam);

            var gameCreated = _repositoryWrapper.GameRepository.CreateNew(game.Date, firstTeamGameSummary,
                secondTeamGameSummary, game.DurationMinutes);

            _repositoryWrapper.UpdateDB();
            Log.Information("Finished game creation request: Game {0} Created.", gameCreated.Code);
            return CreatedAtAction(nameof(FindGame), new { code = gameCreated.Code }, gameCreated);
        }

        [HttpPost("start")]
        public async Task<IActionResult> StartTheGame([FromBody]GameCodeDTO gameCodeDTO)
        {
            Log.Information("Game start request started on game {0}", gameCodeDTO.Code);
            var code = gameCodeDTO.Code;

            var game = _repositoryWrapper.GameRepository.FindByCodeDetailed(code);
            if (game == null)
            {
                Log.Error("Invalid game start request: Game {0} not found.", gameCodeDTO.Code);
                return NotFound();
            }
            game.State = GameState.Going;

            var startGameDTO = new StartGameDTO();
            _mapper.Map(game, startGameDTO);
            _mapper.Map<IList<TeamGameSummary>, IList<StartGameDTO.Team>>(game.TeamGameSummaries, startGameDTO.Teams);

            var message = JsonConvert.SerializeObject(startGameDTO);

            HttpClient client = new HttpClient();
            var response = await client.PostAsync(new Uri("https://localhost:5001/v1a/create", UriKind.Absolute),
                new StringContent(message, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("Invalid game start request ({0}): GameService Error.", gameCodeDTO.Code);
                return BadRequest();
            }
            _repositoryWrapper.UpdateDB();
            Log.Information("Finished game start request: The Game has been started.");
            return Ok();
        }


        [HttpPost("finish")]
        public IActionResult FinishGame([FromBody] FinishGameDTO finishGameDTO)
        {

            Log.Information("Attempt to finish a game with code {}");
            var game = _repositoryWrapper.GameRepository.FindByCodeDetailed(finishGameDTO.GameCode);

            if (game == null)
            {
                Log.Warning($"Game {finishGameDTO.GameCode} has not been found");
                return BadRequest();
            }

            //mapping DTO into a model
            _mapper.Map(finishGameDTO, game);
            //setting a winner
            game.TeamGameSummaries.Find(x => string.Equals(x.Team.Code, finishGameDTO.WinnerCode)).IsWinner = true;
            //setting game status to FINISHED
            game.State = GameState.Finished;
            _repositoryWrapper.UpdateDB();
            
            return Ok();
        }

        [HttpGet("{code}")]
        public IActionResult FindGame(string code)
        {
            Log.Information("FindGame request received on given code {0}", code);
            var game = _repositoryWrapper.GameRepository.FindByCodeDetailed(code);
            if (game == null)
            {
                Log.Error("Invalid FindGame request: Game {0} not found.", code);
                return NotFound();
            }
            Log.Information("Finished FindGame request on game {0}", code);
            return Ok(game);
        }

        [HttpPut("update/{code}")]
        public ActionResult Update(string code, [FromBody]GameUpdateDTO updatedGameUpdate)
        {
            if (code != updatedGameUpdate.Code)
            {
                Log.Error("GameNotFound:code != updatedGameUpdate.Code");
                return NotFound();
            }
            if (!ModelState.IsValid)
            {
                return BadRequest("Invalid Object Properties");
            }

            var gameEntity = _repositoryWrapper.GameRepository.GetListQueryable
                .AsNoTracking().FirstOrDefault(x => x.Code == code);
            if (gameEntity == null)
            {
                Log.Error("GameNotFound:gameEntity==null");
                return NotFound();
            }
            _mapper.Map(updatedGameUpdate, gameEntity);

            _repositoryWrapper.GameRepository.Update(gameEntity);
            _repositoryWrapper.UpdateDB();
            Log.Information("Game {0} has been updated.",code);
            return NoContent();
        }


        [HttpDelete("delete/{code}")]
        public ActionResult<Game> Delete(string code)
        {
            Log.Warning("Game DELETE request started on game {0}.", code);
            var game = _repositoryWrapper.GameRepository.FindByCodeDetailed(code);
            if (game == null)
            {
                Log.Error("Invalid game delete request: Game {0} was not found.", code);
                return NotFound();
            }
            var summary = game.TeamGameSummaries;

            foreach (var x in summary) _repositoryWrapper.TeamGameSummaryRepository.Delete(x);
            _repositoryWrapper.GameRepository.DeleteRecord(game);
            _repositoryWrapper.UpdateDB();
            Log.Warning("Finished game delete request: Game {0} has been deleted.", code);
            return game;
        }
    }
}