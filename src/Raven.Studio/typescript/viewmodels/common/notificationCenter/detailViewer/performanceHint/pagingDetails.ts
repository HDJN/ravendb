import app = require("durandal/app");
import abstractNotification = require("common/notifications/models/abstractNotification");
import notificationCenter = require("common/notifications/notificationCenter");
import performanceHint = require("common/notifications/models/performanceHint");
import abstractPerformanceHintDetails = require("viewmodels/common/notificationCenter/detailViewer/performanceHint/abstractPerformanceHintDetails");
import virtualGridController = require("widgets/virtualGrid/virtualGridController");
import textColumn = require("widgets/virtualGrid/columns/textColumn");
import columnPreviewPlugin = require("widgets/virtualGrid/columnPreviewPlugin");

type pagingDetailsItemDto = {
    Action: string;
    NumberOfResults: number;
    PageSize: number;
    Occurrence: string;
    Duration: number;
    Details: string;
}

class pagingDetails extends abstractPerformanceHintDetails {

    tableItems = [] as pagingDetailsItemDto[];
    private gridController = ko.observable<virtualGridController<pagingDetailsItemDto>>();
    private columnPreview = new columnPreviewPlugin<pagingDetailsItemDto>();

    constructor(hint: performanceHint, notificationCenter: notificationCenter) {
        super(hint, notificationCenter);

        this.tableItems = this.mapItems(hint.details() as Raven.Server.NotificationCenter.Notifications.Details.PagingPerformanceDetails);
        
        // newest first
        this.tableItems.reverse();
    }

    compositionComplete() {
        super.compositionComplete();

        const grid = this.gridController();
        grid.headerVisible(true);

        grid.init((s, t) => this.fetcher(s, t), () => {
            return [
                new textColumn<pagingDetailsItemDto>(grid, x => x.Action, "Action", "20%"),
                new textColumn<pagingDetailsItemDto>(grid, x => x.NumberOfResults ? x.NumberOfResults.toLocaleString() : 'n/a', "# of results", "10%"),
                new textColumn<pagingDetailsItemDto>(grid, x => x.PageSize ? x.PageSize.toLocaleString() : 'n/a', "Page size", "10%"),
                new textColumn<pagingDetailsItemDto>(grid, x => x.Occurrence, "Date", "15%"),
                new textColumn<pagingDetailsItemDto>(grid, x => x.Duration, "Duration (ms)", "15%"),
                new textColumn<pagingDetailsItemDto>(grid, x => x.Details, "Details", "30%")
            ];
        });

        this.columnPreview.install(".pagingDetails", ".paging-details-tooltip", (details: pagingDetailsItemDto, column: textColumn<pagingDetailsItemDto>, e: JQueryEventObject, onValue: (context: any) => void) => {
            const value = column.getCellValue(details);
            if (!_.isUndefined(value)) {
                onValue(value);  
            }
        });
    }

    private fetcher(skip: number, take: number): JQueryPromise<pagedResult<pagingDetailsItemDto>> {
        return $.Deferred<pagedResult<pagingDetailsItemDto>>()
            .resolve({
                items: this.tableItems,
                totalResultCount: this.tableItems.length
            });
    }

    private mapItems(details: Raven.Server.NotificationCenter.Notifications.Details.PagingPerformanceDetails): pagingDetailsItemDto[] {
        return _.flatMap(details.Actions, (value, key) => {
            return value.map(item => 
                ({
                    Action: key,
                    NumberOfResults: item.NumberOfResults,
                    Occurrence: item.Occurrence,
                    PageSize: item.PageSize,
                    Duration: item.Duration,
                    Details: item.Details
                } as pagingDetailsItemDto));
        });
    }

    static supportsDetailsFor(notification: abstractNotification) {
        return (notification instanceof performanceHint) && notification.hintType() === "Paging";
    }

    static showDetailsFor(hint: performanceHint, center: notificationCenter) {
        return app.showBootstrapDialog(new pagingDetails(hint, center));
    }
}

export = pagingDetails;
