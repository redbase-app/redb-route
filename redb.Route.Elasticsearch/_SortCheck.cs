using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.MSearch;
using Elastic.Clients.Elasticsearch.Core.Search;

class SortCheck
{
    void Check()
    {
        var body = new MultisearchBody();
        // Sort is ICollection<SortOptions>
        body.Sort = new List<SortOptions>
        {
            new SortOptions { Field = new FieldSort { Field = "timestamp", Order = SortOrder.Desc } }
        };
        // Source is SourceConfig
        body.Source = new SourceConfig(new SourceFilter
        {
            Includes = new Field[] { new Field("title"), new Field("author") }
        });
        // Source with false (disable)
        body.Source = new SourceConfig(false);
    }
}
